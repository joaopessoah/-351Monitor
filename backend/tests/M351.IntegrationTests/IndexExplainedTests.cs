using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Aggregation;
using M351.Infrastructure.Intervalization;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// GET /api/v1/dashboard/index-explained (item 1 do estudo): "por que o índice mudou",
/// decomposto por APLICATIVO, EQUIPE e DIA.
///
/// O contrato central é a SOMA — as parcelas fecham a variação total, sem fatia "outros". A
/// matemática está coberta em IndexDecompositionTests; aqui o que se prova é que o SQL entrega
/// à fórmula os segundos certos: precedência de classificação da F5, recorte dos dois períodos
/// e a régua de datas dos demais endpoints históricos.
/// </summary>
[Collection(ApiCollection.Name)]
public class IndexExplainedTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base = new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);

    private static DateTimeOffset T(int h, int m) => Base.AddHours(h).AddMinutes(m);

    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    private async Task RunPipelineAsync()
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new IntervalizationService(dataSource).RunOnceAsync();
        await new DailyAggregationService(dataSource).RunOnceAsync();
    }

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient client, string token, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, url, token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"esperado {expected}, veio {response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    /// <summary>Cria a categoria com a classificação pedida e mapeia o app do catálogo nela (regra da ORGANIZAÇÃO).</summary>
    private async Task MapAppAsync(Guid tenantId, string processName, int classification)
    {
        var categoryId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "INSERT INTO categories (id, tenant_id, name, classification) VALUES (@c, @t, @n, @cls)",
            ("c", categoryId), ("t", tenantId), ("n", $"Cat{classification}-{processName}"), ("cls", classification));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            """
            INSERT INTO tenant_app_categories (tenant_id, app_id, category_id)
            SELECT @t, a.id, @c FROM app_catalog a WHERE a.process_name = @p
            ON CONFLICT (tenant_id, app_id) DO UPDATE SET category_id = EXCLUDED.category_id
            """,
            ("t", tenantId), ("c", categoryId), ("p", processName));
    }

    /// <summary>Reagrega depois de mexer na classificação (o mapeamento não reprocessa sozinho).</summary>
    private async Task ReaggregateAsync(Guid tenantId)
    {
        await using var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new ReaggregationRequester(ds).RequestLast30DaysAsync(tenantId);
        await new DailyAggregationService(ds).RunOnceAsync();
    }

    /// <summary>
    /// Cenário canônico dos testes: no dia anterior só o app produtivo (15 min); no dia corrente
    /// o mesmo produtivo (15 min) MAIS 15 min de um app improdutivo.
    ///
    /// Índice anterior = 900/900 = 1,00 · índice atual = 900/1800 = 0,50 → −50 pontos, e a queda
    /// é INTEIRAMENTE do app improdutivo, que não tem um segundo produtivo nos dois períodos.
    /// </summary>
    private async Task<(Guid TenantId, HttpClient Client, string Token, string Day)> SeedQuedaDeIndiceAsync(string prefixo)
    {
        var org = await fixture.CreateOrganizationAsync($"{prefixo} {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: $"NB-{prefixo}");

        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            // ---- dia anterior: 15 min de erp
            f.Event("ACTIVE_WINDOW_CHANGED", T(-24 + 12, 0), new Dictionary<string, object?> { ["process_name"] = "ixp-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(-24 + 12, 5), new Dictionary<string, object?> { ["process_name"] = "ixp-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(-24 + 12, 10), new Dictionary<string, object?> { ["process_name"] = "ixp-erp.exe" }),
            f.Event("LOCK", T(-24 + 12, 15)),
            // ---- dia corrente: 15 min de erp + 15 min de zap
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 0), new Dictionary<string, object?> { ["process_name"] = "ixp-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 5), new Dictionary<string, object?> { ["process_name"] = "ixp-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 10), new Dictionary<string, object?> { ["process_name"] = "ixp-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 15), new Dictionary<string, object?> { ["process_name"] = "ixp-zap.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 20), new Dictionary<string, object?> { ["process_name"] = "ixp-zap.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 25), new Dictionary<string, object?> { ["process_name"] = "ixp-zap.exe" }),
            f.Event("LOCK", T(12, 30)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();

        await MapAppAsync(org.Id, "ixp-erp.exe", 1);
        await MapAppAsync(org.Id, "ixp-zap.exe", -1);
        await ReaggregateAsync(org.Id);

        return (org.Id, client, token, LocalDate(T(12, 0)));
    }

    // ------------------------------------------------------------------ a soma é o contrato

    [Fact]
    public async Task Parcelas_Por_App_Somam_A_Variacao_Total()
    {
        var (_, client, token, day) = await SeedQuedaDeIndiceAsync("IxpApp");

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/index-explained?from={day}&to={day}");
        var root = doc.RootElement;

        var total = root.GetProperty("delta_points").GetDouble();
        var soma = root.GetProperty("by_app").GetProperty("items").EnumerateArray().Sum(e => e.GetProperty("points").GetDouble());

        Assert.Equal(-50d, total, precision: 6);
        Assert.Equal(total, soma, precision: 6);
    }

    [Fact]
    public async Task Aplicativo_Improdutivo_Que_Cresceu_Leva_A_Queda_Inteira()
    {
        var (_, client, token, day) = await SeedQuedaDeIndiceAsync("IxpCulp");

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/index-explained?from={day}&to={day}");

        var apps = doc.RootElement.GetProperty("by_app").GetProperty("items").EnumerateArray().ToList();
        var zap = apps.Single(e => e.GetProperty("key").GetString() == "ixp-zap.exe");
        var erp = apps.Single(e => e.GetProperty("key").GetString() == "ixp-erp.exe");

        Assert.Equal(-50d, zap.GetProperty("points").GetDouble(), precision: 6);
        Assert.Equal(0d, erp.GetProperty("points").GetDouble(), precision: 6);

        // os segundos viajam junto para a tela dizer "mais 15 min" ao lado de "−50 pontos"
        Assert.Equal(900, zap.GetProperty("seconds_classified").GetInt64());
        Assert.Equal(0, zap.GetProperty("seconds_classified_previous").GetInt64());
    }

    [Fact]
    public async Task Parcelas_Por_Equipe_E_Por_Dia_Tambem_Somam_A_Variacao_Total()
    {
        var (_, client, token, day) = await SeedQuedaDeIndiceAsync("IxpDim");

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/index-explained?from={day}&to={day}");
        var root = doc.RootElement;
        var total = root.GetProperty("delta_points").GetDouble();

        Assert.Equal(total, root.GetProperty("by_team").GetProperty("items").EnumerateArray()
            .Sum(e => e.GetProperty("points").GetDouble()), precision: 6);
        Assert.Equal(total, root.GetProperty("by_day").GetProperty("items").EnumerateArray()
            .Sum(e => e.GetProperty("points").GetDouble()), precision: 6);
    }

    [Fact]
    public async Task Classificacao_Mudada_Sem_Recalcular_Historico_E_DECLARADA_Na_Dimensao_De_App()
    {
        var (tenantId, client, token, day) = await SeedQuedaDeIndiceAsync("IxpDiv");

        // O CONTRATO EM RISCO: delta_points sai dos baldes JÁ AGREGADOS (classificação
        // congelada no momento da agregação), enquanto a decomposição por APLICATIVO re-deriva
        // a classificação NA LEITURA. Mexer na classificação sem recalcular o histórico deixa
        // os dois com denominadores diferentes — e aí a soma das parcelas por app não fecha o
        // delta do cabeçalho. Isso não pode acontecer em silêncio: a promessa do painel é
        // justamente que as parcelas fecham.
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            """
            DELETE FROM tenant_app_categories
            WHERE tenant_id = @t
              AND app_id IN (SELECT id FROM app_catalog WHERE process_name = 'ixp-zap.exe')
            """,
            ("t", tenantId));

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/index-explained?from={day}&to={day}");
        var root = doc.RootElement;
        var porApp = root.GetProperty("by_app");

        // o cabeçalho continua vindo dos agregados: −50 pontos
        Assert.Equal(-50d, root.GetProperty("delta_points").GetDouble(), precision: 6);

        // a dimensão de app declara o delta que ELA explica, e as parcelas dela fecham ESSE
        Assert.Equal(
            porApp.GetProperty("delta_points").GetDouble(),
            porApp.GetProperty("items").EnumerateArray().Sum(e => e.GetProperty("points").GetDouble()),
            precision: 6);

        // e a divergência é DITA, com o caminho para resolver
        var divergencia = porApp.GetProperty("divergence").GetString();
        Assert.False(string.IsNullOrWhiteSpace(divergencia), "a divergência tinha de ser declarada");
        Assert.Contains("hist", divergencia!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sem_Divergencia_As_Tres_Dimensoes_Declaram_O_Mesmo_Delta()
    {
        var (_, client, token, day) = await SeedQuedaDeIndiceAsync("IxpConv");

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/index-explained?from={day}&to={day}");
        var root = doc.RootElement;
        var cabecalho = root.GetProperty("delta_points").GetDouble();

        foreach (var dimensao in new[] { "by_app", "by_team", "by_day" })
        {
            var bloco = root.GetProperty(dimensao);
            Assert.Equal(cabecalho, bloco.GetProperty("delta_points").GetDouble(), precision: 4);
            Assert.Equal(JsonValueKind.Null, bloco.GetProperty("divergence").ValueKind);
            Assert.Equal(
                bloco.GetProperty("delta_points").GetDouble(),
                bloco.GetProperty("items").EnumerateArray().Sum(e => e.GetProperty("points").GetDouble()),
                precision: 6);
        }
    }

    // ------------------------------------------------------------------ precedência da F5

    [Fact]
    public async Task Regra_Da_Equipe_Vence_A_Da_Organizacao_Na_Decomposicao()
    {
        var (tenantId, client, token, day) = await SeedQuedaDeIndiceAsync("IxpEqp");

        // a organização diz que o zap é IMPRODUTIVO (semeado acima). A equipe da pessoa diz que
        // é PRODUTIVO — atendimento por WhatsApp é o trabalho dela. A precedência F5 manda a
        // regra da equipe vencer, então a queda tem de desaparecer.
        var teamId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "INSERT INTO teams (id, tenant_id, name) VALUES (@id, @t, 'Comercial')",
            ("id", teamId), ("t", tenantId));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "INSERT INTO team_members (tenant_id, team_id, windows_sid) VALUES (@t, @id, @sid)",
            ("t", tenantId), ("id", teamId), ("sid", EventFactory.DefaultSid));

        var categoryId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "INSERT INTO categories (id, tenant_id, name, classification) VALUES (@c, @t, 'Atendimento', 1)",
            ("c", categoryId), ("t", tenantId));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            """
            INSERT INTO tenant_app_team_categories (tenant_id, team_id, app_id, category_id)
            SELECT @t, @tm, a.id, @c FROM app_catalog a WHERE a.process_name = 'ixp-zap.exe'
            """,
            ("t", tenantId), ("tm", teamId), ("c", categoryId));
        await ReaggregateAsync(tenantId);

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/index-explained?from={day}&to={day}");

        // com o zap produtivo para a equipe, o índice fica em 100% nos dois dias: nada mudou
        Assert.Equal(0d, doc.RootElement.GetProperty("delta_points").GetDouble(), precision: 6);
        Assert.Equal(0d, doc.RootElement.GetProperty("by_app").GetProperty("items").EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == "ixp-zap.exe")
            .GetProperty("points").GetDouble(), precision: 6);
    }

    [Fact]
    public async Task Equipe_De_Verdade_Nomeia_A_Parcela_Em_Vez_De_Sem_Equipe()
    {
        var (tenantId, client, token, day) = await SeedQuedaDeIndiceAsync("IxpNome");

        var teamId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "INSERT INTO teams (id, tenant_id, name) VALUES (@id, @t, 'Comercial')",
            ("id", teamId), ("t", tenantId));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "INSERT INTO team_members (tenant_id, team_id, windows_sid) VALUES (@t, @id, @sid)",
            ("t", tenantId), ("id", teamId), ("sid", EventFactory.DefaultSid));

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/index-explained?from={day}&to={day}");

        Assert.Contains(doc.RootElement.GetProperty("by_team").GetProperty("items").EnumerateArray(),
            e => e.GetProperty("label").GetString() == "Comercial");
    }

    // ------------------------------------------------------------------ ausências e régua

    [Fact]
    public async Task Sem_Periodo_Anterior_Devolve_Unavailable_E_Nunca_Zero()
    {
        var org = await fixture.CreateOrganizationAsync($"IxpVaz {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        var day = LocalDate(T(12, 0));
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/index-explained?from={day}&to={day}");
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("delta_points").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("unavailable").GetString()));
        Assert.Empty(root.GetProperty("by_app").GetProperty("items").EnumerateArray());
        Assert.Empty(root.GetProperty("by_team").GetProperty("items").EnumerateArray());
        Assert.Empty(root.GetProperty("by_day").GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Periodo_Anterior_Tem_A_Mesma_Duracao_E_Termina_Na_Vespera()
    {
        var (_, client, token, day) = await SeedQuedaDeIndiceAsync("IxpPer");

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/index-explained?from={day}&to={day}");

        var anterior = doc.RootElement.GetProperty("previous_period");
        var esperado = DateOnly.ParseExact(day, "yyyy-MM-dd").AddDays(-1).ToString("yyyy-MM-dd");
        Assert.Equal(esperado, anterior.GetProperty("from").GetString());
        Assert.Equal(esperado, anterior.GetProperty("to").GetString());
        Assert.Equal(1, anterior.GetProperty("days").GetInt32());
    }

    [Fact]
    public async Task Janela_Maior_Que_92_Dias_Da_400()
    {
        var org = await fixture.CreateOrganizationAsync($"IxpJan {Guid.NewGuid():N}"[..20]);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        using var _ = await GetJsonAsync(client, token,
            "/api/v1/dashboard/index-explained?from=2026-01-01&to=2026-12-31",
            HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sem_Autenticacao_Da_401()
    {
        var client = fixture.CreateApiClient();
        var day = LocalDate(T(12, 0));
        var response = await client.GetAsync($"/api/v1/dashboard/index-explained?from={day}&to={day}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Limite_Corta_Pelas_Maiores_Contribuicoes_Preservando_O_Sinal()
    {
        var (_, client, token, day) = await SeedQuedaDeIndiceAsync("IxpLim");

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/dashboard/index-explained?from={day}&to={day}&limit=1");

        var apps = doc.RootElement.GetProperty("by_app").GetProperty("items").EnumerateArray().ToList();
        Assert.Single(apps);
        // a maior contribuição em módulo é a do zap, negativa
        Assert.Equal("ixp-zap.exe", apps[0].GetProperty("key").GetString());
        Assert.True(apps[0].GetProperty("points").GetDouble() < 0);
    }
}

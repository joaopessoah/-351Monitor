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
/// F5 — CLASSIFICAÇÃO POR EQUIPE (spec seção 2.4): o MESMO app produtivo numa equipe e
/// improdutivo em outra. O que esta suíte prova, fim a fim (ingestão real → intervalização →
/// agregação):
///
///  1. a ordem de precedência da REGRA: equipe → organização → sem classificação;
///  2. a ordem de precedência da EQUIPE da lane: vínculo da PESSOA → etiqueta legada do
///     dispositivo (devices.tags via teams.tag) → sem equipe;
///  3. que remover a regra da equipe devolve o app à regra da ORGANIZAÇÃO (herança), e não ao
///     balde "sem classificação";
///  4. que a listagem do catálogo com ?team_id devolve a regra EFETIVA e diz de onde ela veio
///     (category_scope), e que sem o parâmetro nada mudou para quem já usava a tela.
/// </summary>
[Collection(ApiCollection.Name)]
public class TeamClassificationTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base =
        new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero); // ontem 00:00Z — dentro da janela N9

    // espaçamento sempre < 600 s: 600 exatos cai na regra de lacuna N7 e vira no_data
    private static DateTimeOffset T(int h, int m) => Base.AddHours(h).AddMinutes(m);

    private async Task<(HttpClient Client, EnrolledDevice Device, Guid TenantId, string AdminToken)> SetupAsync(
        string hostname)
    {
        var org = await fixture.CreateOrganizationAsync($"Eq {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, admin);
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: hostname);
        return (client, device, org.Id, token);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string token, object? body = null)
    {
        using var request = AuthClient.AuthorizedRequest(method, url, token, body);
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"esperado {expected}, veio {response.StatusCode}: {body}");
        return JsonDocument.Parse(string.IsNullOrEmpty(body) ? "null" : body);
    }

    private async Task RunPipelineAsync()
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new IntervalizationService(dataSource).RunOnceAsync();
    }

    private async Task RunAggregationAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new DailyAggregationService(dataSource).RunOnceAsync();
    }

    /// <summary>Marca todos os dias do device como sujos e reagrega (sem esperar o worker).</summary>
    private async Task ReaggregateAsync(Guid tenantId, Guid deviceId)
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            """
            INSERT INTO dirty_days (tenant_id, device_id, day)
            SELECT DISTINCT tenant_id, device_id, source_day FROM activity_intervals
            WHERE tenant_id = @t AND device_id = @d
            ON CONFLICT (tenant_id, device_id, day) DO UPDATE SET day = EXCLUDED.day
            """, ("t", tenantId), ("d", deviceId));
        await RunAggregationAsync();
    }

    private Task<Dictionary<string, object?>?> SummaryAsync(Guid deviceId) =>
        TestDb.RowAsync(fixture.Database.ConnectionString,
            """
            SELECT seconds_active, seconds_work_related, seconds_neutral,
                   seconds_not_work_related, seconds_unclassified
            FROM daily_device_summaries WHERE device_id = @d ORDER BY summary_date LIMIT 1
            """, ("d", deviceId));

    private static int I(object? v) => Convert.ToInt32(v);

    private async Task<Guid> AppIdAsync(string processName) =>
        await TestDb.ScalarAsync<Guid>(fixture.Database.ConnectionString,
            "SELECT id FROM app_catalog WHERE process_name = @p", ("p", processName));

    private async Task<Guid> CategoryAsync(Guid tenantId, string name, int classification)
    {
        var id = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            """
            INSERT INTO categories (id, tenant_id, name, classification) VALUES (@i, @t, @n, @c)
            """, ("i", id), ("t", tenantId), ("n", name), ("c", (short)classification));
        return id;
    }

    private static async Task<Guid> CreateTeamAsync(
        HttpClient client, string token, string name, string? tag = null, object? workHours = null)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/v1/teams", token,
            new { name, tag, work_hours = workHours });
        using var doc = await ReadAsync(response, HttpStatusCode.Created);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Um dia com um único app em foco, para o cálculo ficar legível.</summary>
    private async Task IngestSingleAppDayAsync(HttpClient client, EnrolledDevice device, string processName)
    {
        var f = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f.Event("ACTIVE_WINDOW_CHANGED", T(10, 0), new Dictionary<string, object?> { ["process_name"] = processName }),
            f.Event("LOCK", T(10, 9)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();
    }

    // ==========================================================================================
    // 1) A REGRA DA EQUIPE VENCE A DA ORGANIZAÇÃO — e removê-la devolve o app à regra geral
    // ==========================================================================================
    [Fact]
    public async Task RegraDaEquipe_VenceARegraDaOrganizacao_ERemocaoVoltaAHerdar()
    {
        var (client, device, tenantId, token) = await SetupAsync("NB-EQ-PRECEDENCIA");
        await IngestSingleAppDayAsync(client, device, "eq-figma.exe");

        var appId = await AppIdAsync("eq-figma.exe");
        var produtiva = await CategoryAsync(tenantId, "Design", 1);
        var improdutiva = await CategoryAsync(tenantId, "Distração", -1);

        // regra GERAL: o app é produtivo para a organização inteira
        var geral = await SendAsync(client, HttpMethod.Put, $"/api/v1/app-catalog/{appId}/category", token,
            new { category_id = produtiva });
        (await ReadAsync(geral, HttpStatusCode.OK)).Dispose();

        // a pessoa do dispositivo pertence ao Financeiro, que classifica o mesmo app como -1
        var financeiro = await CreateTeamAsync(client, token, "Financeiro");
        var vincular = await SendAsync(client, HttpMethod.Put, $"/api/v1/teams/{financeiro}/members", token,
            new { windows_sids = new[] { EventFactory.DefaultSid } });
        (await ReadAsync(vincular, HttpStatusCode.OK)).Dispose();

        var daEquipe = await SendAsync(client, HttpMethod.Put, $"/api/v1/app-catalog/{appId}/category", token,
            new { category_id = improdutiva, team_id = financeiro });
        (await ReadAsync(daEquipe, HttpStatusCode.OK)).Dispose();

        await ReaggregateAsync(tenantId, device.DeviceId);

        var comEquipe = await SummaryAsync(device.DeviceId);
        Assert.NotNull(comEquipe);
        Assert.Equal(540, I(comEquipe!["seconds_active"]));
        // a regra da EQUIPE venceu: os 540 s foram para o balde -1, não para o +1 da organização
        Assert.Equal(0, I(comEquipe["seconds_work_related"]));
        Assert.Equal(540, I(comEquipe["seconds_not_work_related"]));
        Assert.Equal(0, I(comEquipe["seconds_unclassified"]));

        // remover a regra DA EQUIPE devolve o app à regra da organização (herança), e não ao
        // balde "sem classificação" — é a diferença entre ausência e negação
        var remover = await SendAsync(client, HttpMethod.Put, $"/api/v1/app-catalog/{appId}/category", token,
            new { category_id = (Guid?)null, team_id = financeiro });
        (await ReadAsync(remover, HttpStatusCode.OK)).Dispose();

        await ReaggregateAsync(tenantId, device.DeviceId);

        var semEquipe = await SummaryAsync(device.DeviceId);
        Assert.NotNull(semEquipe);
        Assert.Equal(540, I(semEquipe!["seconds_work_related"]));
        Assert.Equal(0, I(semEquipe["seconds_not_work_related"]));
        Assert.Equal(0, I(semEquipe["seconds_unclassified"]));
    }

    // ==========================================================================================
    // 2) SEM REGRA DE EQUIPE, o tempo da pessoa vinculada continua na regra da organização;
    //    sem NENHUMA das duas, continua "sem classificação" (o balde não virou neutro)
    // ==========================================================================================
    [Fact]
    public async Task PessoaEmEquipeSemRegraPropria_UsaRegraDaOrganizacao_ESemNenhumaFicaSemClassificacao()
    {
        var (client, device, tenantId, token) = await SetupAsync("NB-EQ-HERANCA");
        await IngestSingleAppDayAsync(client, device, "eq-erp.exe");

        var marketing = await CreateTeamAsync(client, token, "Marketing");
        var vincular = await SendAsync(client, HttpMethod.Put, $"/api/v1/teams/{marketing}/members", token,
            new { windows_sids = new[] { EventFactory.DefaultSid } });
        (await ReadAsync(vincular, HttpStatusCode.OK)).Dispose();

        await ReaggregateAsync(tenantId, device.DeviceId);

        // nenhuma regra em lugar nenhum: sem classificação (jamais neutro)
        var semRegra = await SummaryAsync(device.DeviceId);
        Assert.Equal(540, I(semRegra!["seconds_unclassified"]));
        Assert.Equal(0, I(semRegra["seconds_neutral"]));

        var appId = await AppIdAsync("eq-erp.exe");
        var produtiva = await CategoryAsync(tenantId, "ERP", 1);
        var geral = await SendAsync(client, HttpMethod.Put, $"/api/v1/app-catalog/{appId}/category", token,
            new { category_id = produtiva });
        (await ReadAsync(geral, HttpStatusCode.OK)).Dispose();

        await ReaggregateAsync(tenantId, device.DeviceId);

        // a equipe existe e a pessoa está nela, mas a equipe não declarou nada: herda a geral
        var comGeral = await SummaryAsync(device.DeviceId);
        Assert.Equal(540, I(comGeral!["seconds_work_related"]));
        Assert.Equal(0, I(comGeral["seconds_unclassified"]));
    }

    // ==========================================================================================
    // 3) ETIQUETA LEGADA como atalho: sem vínculo de pessoa, a equipe que declara a tag do
    //    DISPOSITIVO resolve a regra; assim que a pessoa é vinculada a outra equipe, ela vence
    // ==========================================================================================
    [Fact]
    public async Task EtiquetaDoDispositivo_ResolveEquipe_MasVinculoDaPessoaVence()
    {
        var (client, device, tenantId, token) = await SetupAsync("NB-EQ-TAG");
        await IngestSingleAppDayAsync(client, device, "eq-youtube.exe");

        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET tags = ARRAY['comercial'] WHERE id = @d", ("d", device.DeviceId));

        var appId = await AppIdAsync("eq-youtube.exe");
        var neutra = await CategoryAsync(tenantId, "Vídeo", 0);
        var improdutiva = await CategoryAsync(tenantId, "Streaming", -1);

        // Comercial usa a etiqueta legada e classifica o app como improdutivo
        var comercial = await CreateTeamAsync(client, token, "Comercial", tag: "comercial");
        var regraComercial = await SendAsync(client, HttpMethod.Put, $"/api/v1/app-catalog/{appId}/category", token,
            new { category_id = improdutiva, team_id = comercial });
        (await ReadAsync(regraComercial, HttpStatusCode.OK)).Dispose();

        await ReaggregateAsync(tenantId, device.DeviceId);

        var porTag = await SummaryAsync(device.DeviceId);
        Assert.Equal(540, I(porTag!["seconds_not_work_related"]));

        // agora a MESMA pessoa é vinculada a Conteúdo, que classifica o app como neutro:
        // o vínculo da pessoa vence a etiqueta do dispositivo
        var conteudo = await CreateTeamAsync(client, token, "Conteúdo");
        var regraConteudo = await SendAsync(client, HttpMethod.Put, $"/api/v1/app-catalog/{appId}/category", token,
            new { category_id = neutra, team_id = conteudo });
        (await ReadAsync(regraConteudo, HttpStatusCode.OK)).Dispose();
        var vincular = await SendAsync(client, HttpMethod.Put, $"/api/v1/teams/{conteudo}/members", token,
            new { windows_sids = new[] { EventFactory.DefaultSid } });
        (await ReadAsync(vincular, HttpStatusCode.OK)).Dispose();

        await ReaggregateAsync(tenantId, device.DeviceId);

        var porPessoa = await SummaryAsync(device.DeviceId);
        Assert.Equal(0, I(porPessoa!["seconds_not_work_related"]));
        Assert.Equal(540, I(porPessoa["seconds_neutral"]));
    }

    // ==========================================================================================
    // 4) A LISTAGEM DO CATÁLOGO no escopo de equipe: regra efetiva + de onde ela veio
    // ==========================================================================================
    [Fact]
    public async Task Catalogo_ComTeamId_MostraRegraEfetivaEOEscopo_SemTeamIdNadaMuda()
    {
        var (client, device, tenantId, token) = await SetupAsync("NB-EQ-CATALOGO");
        await IngestSingleAppDayAsync(client, device, "eq-slack.exe");

        var appId = await AppIdAsync("eq-slack.exe");
        var comunicacao = await CategoryAsync(tenantId, "Comunicação", 1);
        var ruido = await CategoryAsync(tenantId, "Ruído", -1);

        var geral = await SendAsync(client, HttpMethod.Put, $"/api/v1/app-catalog/{appId}/category", token,
            new { category_id = comunicacao });
        (await ReadAsync(geral, HttpStatusCode.OK)).Dispose();

        var suporte = await CreateTeamAsync(client, token, "Suporte");

        // sem regra própria: a equipe HERDA a regra da organização (scope "organization")
        var herdado = await SendAsync(client, HttpMethod.Get, $"/api/v1/app-catalog?team_id={suporte}", token);
        using (var doc = await ReadAsync(herdado, HttpStatusCode.OK))
        {
            var item = doc.RootElement.GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("app_id").GetGuid() == appId);
            Assert.Equal("organization", item.GetProperty("category_scope").GetString());
            Assert.Equal(comunicacao, item.GetProperty("category").GetProperty("id").GetGuid());
            // app coberto pela regra geral NÃO é pendência da equipe
            Assert.Equal(0, doc.RootElement.GetProperty("uncategorized_count").GetInt32());
        }

        var propria = await SendAsync(client, HttpMethod.Put, $"/api/v1/app-catalog/{appId}/category", token,
            new { category_id = ruido, team_id = suporte });
        (await ReadAsync(propria, HttpStatusCode.OK)).Dispose();

        var comRegra = await SendAsync(client, HttpMethod.Get, $"/api/v1/app-catalog?team_id={suporte}", token);
        using (var doc = await ReadAsync(comRegra, HttpStatusCode.OK))
        {
            var item = doc.RootElement.GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("app_id").GetGuid() == appId);
            Assert.Equal("team", item.GetProperty("category_scope").GetString());
            Assert.Equal(ruido, item.GetProperty("category").GetProperty("id").GetGuid());
        }

        // SEM team_id a tela continua vendo exatamente a regra da organização
        var semEscopo = await SendAsync(client, HttpMethod.Get, "/api/v1/app-catalog", token);
        using (var doc = await ReadAsync(semEscopo, HttpStatusCode.OK))
        {
            var item = doc.RootElement.GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("app_id").GetGuid() == appId);
            Assert.Equal("organization", item.GetProperty("category_scope").GetString());
            Assert.Equal(comunicacao, item.GetProperty("category").GetProperty("id").GetGuid());
        }

        // equipe inexistente no escopo → 404, nunca 403 nem lista vazia silenciosa
        var inexistente = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/app-catalog?team_id={Guid.NewGuid()}", token);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
    }

    // ==========================================================================================
    // 5) Excluir a CATEGORIA leva junto a regra por equipe que apontava para ela (FK)
    // ==========================================================================================
    [Fact]
    public async Task ExcluirCategoria_RemoveTambemAsRegrasPorEquipe()
    {
        var (client, device, tenantId, token) = await SetupAsync("NB-EQ-DELCAT");
        await IngestSingleAppDayAsync(client, device, "eq-steam.exe");

        var appId = await AppIdAsync("eq-steam.exe");
        var jogos = await CategoryAsync(tenantId, "Jogos", -1);
        var time = await CreateTeamAsync(client, token, "TI");

        var regra = await SendAsync(client, HttpMethod.Put, $"/api/v1/app-catalog/{appId}/category", token,
            new { category_id = jogos, team_id = time });
        (await ReadAsync(regra, HttpStatusCode.OK)).Dispose();

        var excluir = await SendAsync(client, HttpMethod.Delete, $"/api/v1/categories/{jogos}", token);
        Assert.Equal(HttpStatusCode.NoContent, excluir.StatusCode);

        var restantes = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM tenant_app_team_categories WHERE tenant_id = @t", ("t", tenantId));
        Assert.Equal(0, restantes);
    }

    // ==========================================================================================
    // 6) O LOTE também aceita escopo de equipe (é o "aplicar sugestões" da fila)
    // ==========================================================================================
    [Fact]
    public async Task LoteComTeamId_EscreveRegrasDaEquipe_SemTocarNaRegraDaOrganizacao()
    {
        var (client, device, tenantId, token) = await SetupAsync("NB-EQ-LOTE");
        await IngestSingleAppDayAsync(client, device, "eq-spotify.exe");

        var appId = await AppIdAsync("eq-spotify.exe");
        var neutra = await CategoryAsync(tenantId, "Música", 0);
        var improdutiva = await CategoryAsync(tenantId, "Fora do trabalho", -1);
        var time = await CreateTeamAsync(client, token, "Operações");

        var geral = await SendAsync(client, HttpMethod.Put, $"/api/v1/app-catalog/{appId}/category", token,
            new { category_id = neutra });
        (await ReadAsync(geral, HttpStatusCode.OK)).Dispose();

        var lote = await SendAsync(client, HttpMethod.Put, "/api/v1/app-catalog/categories/batch", token,
            new { team_id = time, items = new[] { new { app_id = appId, category_id = improdutiva } } });
        using (var doc = await ReadAsync(lote, HttpStatusCode.OK))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("applied").GetInt32());
            Assert.Equal(time, doc.RootElement.GetProperty("items")[0].GetProperty("team_id").GetGuid());
        }

        // a regra da ORGANIZAÇÃO continua sendo a neutra: o lote de equipe não a toca
        var orgRule = await TestDb.ScalarAsync<Guid>(fixture.Database.ConnectionString,
            "SELECT category_id FROM tenant_app_categories WHERE tenant_id = @t AND app_id = @a",
            ("t", tenantId), ("a", appId));
        Assert.Equal(neutra, orgRule);

        var teamRule = await TestDb.ScalarAsync<Guid>(fixture.Database.ConnectionString,
            "SELECT category_id FROM tenant_app_team_categories WHERE tenant_id = @t AND app_id = @a",
            ("t", tenantId), ("a", appId));
        Assert.Equal(improdutiva, teamRule);
    }
}

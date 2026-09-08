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
/// GET /api/v1/people/{sid}/self-view (item 2 do estudo, decisão 10 do spec).
///
/// A VISÃO DO COLABORADOR — os mesmos números que a pessoa veria sobre si — servida DENTRO do
/// painel, para quem já tem acesso. O colaborador não é usuário do sistema: não há login, token
/// pessoal nem rota pública com dado dele, e este endpoint exige Viewer+ autenticado como
/// qualquer outra leitura de dado pessoal.
///
/// Índice e cobertura saem CALCULADOS DO SERVIDOR, pela fórmula única da decisão 4. Antes disso
/// a página da pessoa refazia a conta no cliente, o que é duas fórmulas para manter em sincronia
/// — a pendência que este endpoint fecha.
/// </summary>
[Collection(ApiCollection.Name)]
public class PersonSelfViewTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Base = new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);

    private static DateTimeOffset T(int h, int m) => Base.AddHours(h).AddMinutes(m);

    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient client, string token, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, url, token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"esperado {expected}, veio {response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// Uma pessoa com 15 min produtivos (erp), 5 min improdutivos (zap) e 5 min sem
    /// classificação (misterio) num dia. Índice = 900/1200 = 0,75; cobertura = 1500/1500... não:
    /// (1500 − 300)/1500 = 0,80.
    /// </summary>
    private async Task<(Guid TenantId, HttpClient Client, string Token, string Day, Guid DeviceUserId)>
        SeedPessoaAsync(string prefixo)
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
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 0), new Dictionary<string, object?> { ["process_name"] = "sv-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 5), new Dictionary<string, object?> { ["process_name"] = "sv-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 10), new Dictionary<string, object?> { ["process_name"] = "sv-erp.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 15), new Dictionary<string, object?> { ["process_name"] = "sv-zap.exe" }),
            f.Event("ACTIVE_WINDOW_CHANGED", T(12, 20), new Dictionary<string, object?> { ["process_name"] = "sv-misterio.exe" }),
            f.Event("LOCK", T(12, 25)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();

        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using (var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            await new IntervalizationService(ds).RunOnceAsync();
            await new DailyAggregationService(ds).RunOnceAsync();
        }

        foreach (var (processo, classificacao) in new[] { ("sv-erp.exe", 1), ("sv-zap.exe", -1) })
        {
            var categoryId = Uuid7.NewUuid7();
            await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
                "INSERT INTO categories (id, tenant_id, name, classification) VALUES (@c, @t, @n, @cls)",
                ("c", categoryId), ("t", org.Id), ("n", $"Cat{classificacao}-{processo}"), ("cls", classificacao));
            await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
                """
                INSERT INTO tenant_app_categories (tenant_id, app_id, category_id)
                SELECT @t, a.id, @c FROM app_catalog a WHERE a.process_name = @p
                ON CONFLICT (tenant_id, app_id) DO UPDATE SET category_id = EXCLUDED.category_id
                """,
                ("t", org.Id), ("c", categoryId), ("p", processo));
        }

        await using (var ds = NpgsqlDataSource.Create(fixture.Database.ConnectionString))
        {
            await new ReaggregationRequester(ds).RequestLast30DaysAsync(org.Id);
            await new DailyAggregationService(ds).RunOnceAsync();
        }

        var deviceUserId = await TestDb.ScalarAsync<Guid>(fixture.Database.ConnectionString,
            "SELECT id FROM device_users WHERE tenant_id = @t AND windows_sid = @sid",
            ("t", org.Id), ("sid", EventFactory.DefaultSid));

        return (org.Id, client, token, LocalDate(T(12, 0)), deviceUserId);
    }

    // ------------------------------------------------------------------ os números da pessoa

    [Fact]
    public async Task SelfView_Traz_Indice_E_Cobertura_Calculados_No_Servidor()
    {
        var (_, client, token, day, _) = await SeedPessoaAsync("SvIdx");

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/people/{EventFactory.DefaultSid}/self-view?from={day}&to={day}");
        var root = doc.RootElement;

        Assert.Equal(25 * 60, root.GetProperty("seconds_active").GetInt64());
        Assert.Equal(15 * 60, root.GetProperty("seconds_work_related").GetInt64());
        Assert.Equal(5 * 60, root.GetProperty("seconds_not_work_related").GetInt64());
        Assert.Equal(5 * 60, root.GetProperty("seconds_unclassified").GetInt64());
        Assert.Equal(0, root.GetProperty("seconds_neutral").GetInt64());

        // índice = 900 / (900 + 0 + 300) = 0,75 · cobertura = (1500 − 300) / 1500 = 0,80
        Assert.Equal(0.75, root.GetProperty("productivity_index").GetDouble(), 4);
        Assert.Equal(0.80, root.GetProperty("classification_coverage").GetDouble(), 4);

        Assert.Equal(EventFactory.DefaultSid, root.GetProperty("windows_sid").GetString());
        Assert.Equal(1, root.GetProperty("days_with_data").GetInt32());
    }

    [Fact]
    public async Task SelfView_Traz_Os_Aplicativos_Da_Pessoa_Com_A_Classificacao()
    {
        var (_, client, token, day, _) = await SeedPessoaAsync("SvApps");

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/people/{EventFactory.DefaultSid}/self-view?from={day}&to={day}");

        var apps = doc.RootElement.GetProperty("top_apps").EnumerateArray().ToList();
        var erp = apps.Single(a => a.GetProperty("process_name").GetString() == "sv-erp.exe");

        Assert.Equal(900, erp.GetProperty("seconds_active").GetInt64());
        Assert.Equal(1, erp.GetProperty("classification").GetInt32());

        // o app sem regra aparece com classificação NULL, não com zero: zero é "neutro", e
        // dizer neutro onde ninguém classificou seria inventar uma decisão da empresa
        var misterio = apps.Single(a => a.GetProperty("process_name").GetString() == "sv-misterio.exe");
        Assert.Equal(JsonValueKind.Null, misterio.GetProperty("classification").ValueKind);
    }

    [Fact]
    public async Task SelfView_Aceita_O_Sid_E_O_DeviceUserId_E_Devolve_O_Sid_Canonico()
    {
        var (_, client, token, day, deviceUserId) = await SeedPessoaAsync("SvIds");

        using var porSid = await GetJsonAsync(client, token,
            $"/api/v1/people/{EventFactory.DefaultSid}/self-view?from={day}&to={day}");
        using var porDeviceUser = await GetJsonAsync(client, token,
            $"/api/v1/people/{deviceUserId}/self-view?from={day}&to={day}");

        // a página da pessoa navega por device_user_id e GET /device-users/{id} não devolve o
        // SID: a resolução acontece no servidor para nunca mostrar a pessoa errada
        Assert.Equal(
            porSid.RootElement.GetProperty("windows_sid").GetString(),
            porDeviceUser.RootElement.GetProperty("windows_sid").GetString());
        Assert.Equal(
            porSid.RootElement.GetProperty("seconds_active").GetInt64(),
            porDeviceUser.RootElement.GetProperty("seconds_active").GetInt64());
    }

    // ------------------------------------------------------------------ ausências

    [Fact]
    public async Task SelfView_Sem_Dado_Devolve_Indicadores_Null_E_Nunca_Zero()
    {
        var (_, client, token, _, _) = await SeedPessoaAsync("SvVaz");

        // um dia sem nenhuma atividade da pessoa
        var vazio = DateOnly.ParseExact(LocalDate(T(12, 0)), "yyyy-MM-dd").AddDays(-40).ToString("yyyy-MM-dd");
        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/people/{EventFactory.DefaultSid}/self-view?from={vazio}&to={vazio}");

        Assert.Equal(0, doc.RootElement.GetProperty("seconds_active").GetInt64());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("productivity_index").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("classification_coverage").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("days_with_data").GetInt32());
    }

    [Fact]
    public async Task SelfView_De_Pessoa_Inexistente_Da_404()
    {
        var (_, client, token, day, _) = await SeedPessoaAsync("SvNao");

        using var _2 = await GetJsonAsync(client, token,
            $"/api/v1/people/S-1-5-21-0-0-0-9999/self-view?from={day}&to={day}",
            HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SelfView_De_Outro_Tenant_Da_404_E_Nunca_403()
    {
        var (_, _, _, day, deviceUserId) = await SeedPessoaAsync("SvTen");

        // outro tenant, com o id da pessoa do primeiro: recurso de outro dono responde 404
        var outra = await fixture.CreateOrganizationAsync($"SvOut {Guid.NewGuid():N}"[..20]);
        var intruso = await fixture.CreateUserAsync(outra.Id, UserRole.Viewer);
        var clientIntruso = fixture.CreateApiClient();
        var tokenIntruso = await AuthClient.LoginAsync(clientIntruso, intruso);

        using var _2 = await GetJsonAsync(clientIntruso, tokenIntruso,
            $"/api/v1/people/{deviceUserId}/self-view?from={day}&to={day}",
            HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SelfView_Sem_Autenticacao_Da_401()
    {
        var (_, _, _, day, _) = await SeedPessoaAsync("SvAuth");

        var anonimo = fixture.CreateApiClient();
        var response = await anonimo.GetAsync(
            $"/api/v1/people/{EventFactory.DefaultSid}/self-view?from={day}&to={day}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------------ auditoria

    [Fact]
    public async Task SelfView_Grava_View_Report_Porque_E_Leitura_De_Dado_Pessoal()
    {
        var (tenantId, client, token, day, _) = await SeedPessoaAsync("SvAud");

        using var _2 = await GetJsonAsync(client, token,
            $"/api/v1/people/{EventFactory.DefaultSid}/self-view?from={day}&to={day}");

        Assert.Equal(1, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            """
            SELECT count(*) FROM audit_log
            WHERE tenant_id = @t AND action = 'view_report' AND target_type = 'people'
            """,
            ("t", tenantId)));
    }

    [Fact]
    public async Task SelfView_Nao_Grava_Auditoria_Quando_A_Pessoa_Nao_Existe()
    {
        var (tenantId, client, token, day, _) = await SeedPessoaAsync("SvAud2");

        using var _2 = await GetJsonAsync(client, token,
            $"/api/v1/people/S-1-5-21-0-0-0-9999/self-view?from={day}&to={day}",
            HttpStatusCode.NotFound);

        // 404 não é leitura de dado pessoal: não houve dado pessoal a ler
        Assert.Equal(0, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            """
            SELECT count(*) FROM audit_log
            WHERE tenant_id = @t AND action = 'view_report' AND target_type = 'people'
            """,
            ("t", tenantId)));
    }

    // ------------------------------------------------------------------ anotações e contestações

    [Fact]
    public async Task SelfView_Conta_As_Anotacoes_E_Contestacoes_Em_Aberto()
    {
        var (tenantId, client, token, day, _) = await SeedPessoaAsync("SvNot");

        // uma anotação e uma contestação, as duas pendentes de revisão do gestor
        foreach (var kind in new[] { "anotacao", "contestacao" })
        {
            using var request = AuthClient.AuthorizedRequest(
                HttpMethod.Post, $"/api/v1/people/{EventFactory.DefaultSid}/notes", token);
            request.Content = JsonContent(new
            {
                kind,
                started_at = T(12, 0).ToString("o"),
                ended_at = T(12, 25).ToString("o"),
                body = $"contexto de teste ({kind})",
            });
            var response = await client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode,
                $"criar {kind} falhou: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        using var doc = await GetJsonAsync(client, token,
            $"/api/v1/people/{EventFactory.DefaultSid}/self-view?from={day}&to={day}");

        Assert.Equal(1, doc.RootElement.GetProperty("open_notes").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("open_disputes").GetInt32());
        _ = tenantId;
    }

    private static HttpContent JsonContent(object value) =>
        new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
}

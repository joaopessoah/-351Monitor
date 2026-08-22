using System.Globalization;
using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// Metas semanais AGREGADAS da organização (F5), no PATCH /api/v1/organization:
/// goal_weekly_active_hours (1 a 10000) e goal_work_related_pct (1 a 100).
///
/// LINHA VERMELHA: a meta é da ORGANIZAÇÃO inteira, nunca por pessoa e nunca comparando
/// pessoas. Por isso os campos moram em organizations, não em users nem em device_users,
/// e nenhum teste aqui assume recorte individual.
///
/// Cobre a mesma régua dos vizinhos de PATCH parcial (TransparencyTests, AgentConfigEndpointTests):
/// valor válido persistindo, os dois extremos de cada faixa, valor fora da faixa recusado sem
/// escrever nada, campo ausente que não muda o valor já gravado, null que REMOVE a meta,
/// trilha update_privacy_config com de→para, gate de papel (Viewer → 403) e isolamento
/// cross-tenant.
/// </summary>
[Collection(ApiCollection.Name)]
public class OrganizationGoalsTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private async Task<(HttpClient Client, Guid TenantId, string AdminToken)> SetupAsync(string orgName)
    {
        var org = await fixture.CreateOrganizationAsync(orgName);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        return (client, org.Id, await AuthClient.LoginAsync(client, admin));
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string token, object body)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Patch, "/api/v1/organization", token, body);
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> GetAsync(HttpClient client, string token, string url)
    {
        using var request = AuthClient.AuthorizedRequest(HttpMethod.Get, url, token);
        var response = await client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"esperado 200 em {url}, veio {response.StatusCode}: {raw}");
        return JsonDocument.Parse(raw);
    }

    /// <summary>Lê a meta direto do banco (::text para distinguir "sem meta" de zero).</summary>
    private async Task<int?> MetaHorasAsync(Guid tenantId) => Parse(await TestDb.ScalarAsync<string>(Cs,
        "SELECT goal_weekly_active_hours::text FROM organizations WHERE id = @id", ("id", tenantId)));

    private async Task<int?> MetaPctAsync(Guid tenantId) => Parse(await TestDb.ScalarAsync<string>(Cs,
        "SELECT goal_work_related_pct::text FROM organizations WHERE id = @id", ("id", tenantId)));

    private static int? Parse(string? raw) =>
        raw is null ? null : int.Parse(raw, CultureInfo.InvariantCulture);

    private async Task<long> TrilhasAsync(Guid tenantId) => await TestDb.ScalarAsync<long>(Cs,
        "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'update_privacy_config'",
        ("t", tenantId));

    // ------------------------------------------------------------ caminho feliz + eco no GET e no /me
    [Fact]
    public async Task Patch_MetasValidas_PersistemEAparecemNoGetENoMe()
    {
        var (client, tenantId, token) = await SetupAsync("Org Metas Feliz");

        // sem meta definida a org nasce com null nos dois campos
        using (var antes = await GetAsync(client, token, "/api/v1/organization"))
        {
            Assert.Equal(JsonValueKind.Null, antes.RootElement.GetProperty("goal_weekly_active_hours").ValueKind);
            Assert.Equal(JsonValueKind.Null, antes.RootElement.GetProperty("goal_work_related_pct").ValueKind);
        }

        var response = await PatchAsync(client, token,
            new { goal_weekly_active_hours = 160, goal_work_related_pct = 70 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal(160, body.RootElement.GetProperty("goal_weekly_active_hours").GetInt32());
            Assert.Equal(70, body.RootElement.GetProperty("goal_work_related_pct").GetInt32());
        }

        Assert.Equal(160, await MetaHorasAsync(tenantId));
        Assert.Equal(70, await MetaPctAsync(tenantId));

        // o GET devolve o que foi gravado e o /me leva as metas para o portal (MetasCard e a
        // barra de progresso da semana leem daqui)
        using (var depois = await GetAsync(client, token, "/api/v1/organization"))
        {
            Assert.Equal(160, depois.RootElement.GetProperty("goal_weekly_active_hours").GetInt32());
            Assert.Equal(70, depois.RootElement.GetProperty("goal_work_related_pct").GetInt32());
        }

        using (var me = await GetAsync(client, token, "/api/v1/me"))
        {
            var org = me.RootElement.GetProperty("organization");
            Assert.Equal(160, org.GetProperty("goal_weekly_active_hours").GetInt32());
            Assert.Equal(70, org.GetProperty("goal_work_related_pct").GetInt32());
        }
    }

    // ------------------------------------------------------------ extremos aceitos de cada faixa
    [Fact]
    public async Task Patch_LimitesDasFaixas_SaoAceitos()
    {
        var (client, tenantId, token) = await SetupAsync("Org Metas Limites");

        // limite inferior das duas faixas
        var inferior = await PatchAsync(client, token,
            new { goal_weekly_active_hours = 1, goal_work_related_pct = 1 });
        Assert.Equal(HttpStatusCode.OK, inferior.StatusCode);
        Assert.Equal(1, await MetaHorasAsync(tenantId));
        Assert.Equal(1, await MetaPctAsync(tenantId));

        // limite superior das duas faixas
        var superior = await PatchAsync(client, token,
            new { goal_weekly_active_hours = 10_000, goal_work_related_pct = 100 });
        Assert.Equal(HttpStatusCode.OK, superior.StatusCode);
        Assert.Equal(10_000, await MetaHorasAsync(tenantId));
        Assert.Equal(100, await MetaPctAsync(tenantId));
    }

    // ------------------------------------------------------------ fora da faixa: 400 e nada gravado
    [Fact]
    public async Task Patch_ForaDaFaixa_Retorna400_SemPersistirNemAuditar()
    {
        var (client, tenantId, token) = await SetupAsync("Org Metas Faixa");

        // valor válido de partida, para provar que a recusa não mexe no que já existia
        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(client, token, new { goal_weekly_active_hours = 120, goal_work_related_pct = 60 })).StatusCode);
        var trilhasAntes = await TrilhasAsync(tenantId);

        object[] corposInvalidos =
        [
            new { goal_weekly_active_hours = 0 },            // abaixo do mínimo
            new { goal_weekly_active_hours = 10_001 },       // acima do máximo
            new { goal_weekly_active_hours = -40 },          // negativo
            new { goal_work_related_pct = 0 },               // abaixo do mínimo
            new { goal_work_related_pct = 101 },             // acima do máximo (não existe 110% de meta)
            new { goal_weekly_active_hours = "160" },        // string onde se espera inteiro
            new { goal_work_related_pct = 70.5 },            // fracionário
        ];

        foreach (var corpo in corposInvalidos)
        {
            var response = await PatchAsync(client, token, corpo);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // valores anteriores intactos e nenhuma trilha nova (a recusa acontece antes de escrever)
        Assert.Equal(120, await MetaHorasAsync(tenantId));
        Assert.Equal(60, await MetaPctAsync(tenantId));
        Assert.Equal(trilhasAntes, await TrilhasAsync(tenantId));
    }

    // ------------------------------------------------------------ ausente não muda; null remove
    [Fact]
    public async Task Patch_CampoAusente_NaoAlteraMetaJaDefinida()
    {
        var (client, tenantId, token) = await SetupAsync("Org Metas Ausente");

        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(client, token, new { goal_weekly_active_hours = 150, goal_work_related_pct = 65 })).StatusCode);

        // PATCH que mexe SÓ na finalidade: as metas ficam exatamente como estavam
        var response = await PatchAsync(client, token,
            new { finalidade_declarada = "Gestão transparente do uso das estações" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal(150, body.RootElement.GetProperty("goal_weekly_active_hours").GetInt32());
            Assert.Equal(65, body.RootElement.GetProperty("goal_work_related_pct").GetInt32());
        }

        Assert.Equal(150, await MetaHorasAsync(tenantId));
        Assert.Equal(65, await MetaPctAsync(tenantId));

        // e um PATCH que mexe só em UMA das metas não derruba a outra
        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(client, token, new { goal_work_related_pct = 80 })).StatusCode);
        Assert.Equal(150, await MetaHorasAsync(tenantId));
        Assert.Equal(80, await MetaPctAsync(tenantId));
    }

    [Fact]
    public async Task Patch_Null_RemoveAMeta_EAuditaDePara()
    {
        var (client, tenantId, token) = await SetupAsync("Org Metas Null");

        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(client, token, new { goal_weekly_active_hours = 200, goal_work_related_pct = 75 })).StatusCode);

        // a trilha da DEFINIÇÃO já traz o de→para (de null para o valor)
        var definicao = await TestDb.ScalarAsync<string>(Cs, """
            SELECT detail::text FROM audit_log
            WHERE tenant_id = @t AND action = 'update_privacy_config' AND target_type = 'organization'
            ORDER BY occurred_at DESC LIMIT 1
            """, ("t", tenantId));
        Assert.NotNull(definicao);
        using (var detail = JsonDocument.Parse(definicao!))
        {
            var horas = detail.RootElement.GetProperty("goal_weekly_active_hours");
            Assert.Equal(JsonValueKind.Null, horas.GetProperty("from").ValueKind);
            Assert.Equal(200, horas.GetProperty("to").GetInt32());
            var pct = detail.RootElement.GetProperty("goal_work_related_pct");
            Assert.Equal(JsonValueKind.Null, pct.GetProperty("from").ValueKind);
            Assert.Equal(75, pct.GetProperty("to").GetInt32());
        }

        // null REMOVE a meta (não é "campo ausente", é decisão explícita de deixar de ter meta)
        var response = await PatchAsync(client, token,
            new { goal_weekly_active_hours = (int?)null, goal_work_related_pct = (int?)null });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("goal_weekly_active_hours").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("goal_work_related_pct").ValueKind);
        }

        Assert.Null(await MetaHorasAsync(tenantId));
        Assert.Null(await MetaPctAsync(tenantId));

        // e a REMOÇÃO também deixa trilha, com o de→para invertido
        var remocao = await TestDb.ScalarAsync<string>(Cs, """
            SELECT detail::text FROM audit_log
            WHERE tenant_id = @t AND action = 'update_privacy_config' AND target_type = 'organization'
            ORDER BY occurred_at DESC LIMIT 1
            """, ("t", tenantId));
        Assert.NotNull(remocao);
        using (var detail = JsonDocument.Parse(remocao!))
        {
            var horas = detail.RootElement.GetProperty("goal_weekly_active_hours");
            Assert.Equal(200, horas.GetProperty("from").GetInt32());
            Assert.Equal(JsonValueKind.Null, horas.GetProperty("to").ValueKind);
            var pct = detail.RootElement.GetProperty("goal_work_related_pct");
            Assert.Equal(75, pct.GetProperty("from").GetInt32());
            Assert.Equal(JsonValueKind.Null, pct.GetProperty("to").ValueKind);
        }

        // repetir a remoção não gera trilha nova (nada mudou)
        var trilhas = await TrilhasAsync(tenantId);
        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(client, token, new { goal_weekly_active_hours = (int?)null })).StatusCode);
        Assert.Equal(trilhas, await TrilhasAsync(tenantId));
    }

    // ------------------------------------------------------------ gate de papel
    [Fact]
    public async Task Patch_Viewer_Recebe403_SemPersistir()
    {
        var org = await fixture.CreateOrganizationAsync("Org Metas Viewer");
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var token = await AuthClient.LoginAsync(client, viewer);

        var response = await PatchAsync(client, token, new { goal_weekly_active_hours = 160 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        Assert.Null(await MetaHorasAsync(org.Id));
        Assert.Equal(0L, await TrilhasAsync(org.Id));

        // o Viewer continua LENDO a meta (o portal mostra o progresso para todo mundo)
        using var body = await GetAsync(client, token, "/api/v1/organization");
        Assert.True(body.RootElement.TryGetProperty("goal_weekly_active_hours", out _));
    }

    // ------------------------------------------------------------ isolamento cross-tenant
    [Fact]
    public async Task Patch_MetasNaoVazamEntreTenants()
    {
        // não há id de org na rota: o PATCH sempre edita a org do TOKEN
        var (clientA, tenantA, tokenA) = await SetupAsync("Org Metas Iso A");
        var (_, tenantB, tokenB) = await SetupAsync("Org Metas Iso B");

        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(clientA, tokenB, new { goal_weekly_active_hours = 999, goal_work_related_pct = 33 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await PatchAsync(clientA, tokenA, new { goal_weekly_active_hours = 111, goal_work_related_pct = 22 })).StatusCode);

        // cada org guarda a própria meta
        Assert.Equal(111, await MetaHorasAsync(tenantA));
        Assert.Equal(22, await MetaPctAsync(tenantA));
        Assert.Equal(999, await MetaHorasAsync(tenantB));
        Assert.Equal(33, await MetaPctAsync(tenantB));

        // e o GET de A jamais devolve a meta de B
        using var body = await GetAsync(clientA, tokenA, "/api/v1/organization");
        Assert.Equal(111, body.RootElement.GetProperty("goal_weekly_active_hours").GetInt32());
        Assert.Equal(22, body.RootElement.GetProperty("goal_work_related_pct").GetInt32());

        // a trilha de cada mudança fica no tenant que a fez
        Assert.Equal(1L, await TrilhasAsync(tenantA));
        Assert.Equal(1L, await TrilhasAsync(tenantB));
    }
}

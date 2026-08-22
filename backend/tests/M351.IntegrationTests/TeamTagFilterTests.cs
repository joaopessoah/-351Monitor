using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// Filtro por etiqueta de equipe (F5, ?tag): "me mostra só o comercial" é a primeira pergunta
/// do gestor com mais de 30 máquinas, e até aqui a resposta era exportar CSV. É filtro de
/// VISUALIZAÇÃO, não escopo de permissão (o papel Manager-por-equipe segue adiado para a v1.1):
/// qualquer papel continua vendo tudo, só escolhe o recorte exibido.
/// </summary>
[Collection(ApiCollection.Name)]
public class TeamTagFilterTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private async Task<(HttpClient Client, Guid TenantId, string Token)> SetupAsync(string orgName)
    {
        var org = await fixture.CreateOrganizationAsync(orgName);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        return (client, org.Id, await AuthClient.LoginAsync(client, viewer));
    }

    private async Task SetTagsAsync(Guid deviceId, params string[] tags) =>
        await TestDb.ExecuteAsync(Cs,
            "UPDATE devices SET tags = @tags WHERE id = @id", ("id", deviceId), ("tags", tags));

    private async Task SeedSummaryAsync(Guid tenantId, Guid deviceId, DateOnly date, int activeSeconds) =>
        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO daily_device_summaries (
                tenant_id, summary_date, device_id, device_user_id,
                seconds_active, seconds_on, computed_at)
            VALUES (@t, @day, @d, @u, @a, @a, now())
            """,
            ("t", tenantId), ("day", date), ("d", deviceId), ("u", Uuid7.NewUuid7()), ("a", activeSeconds));

    [Fact]
    public async Task Summary_ComTag_SomaSoOsDevicesDaEquipe()
    {
        var (client, tenantId, token) = await SetupAsync("Org Tag Summary");
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);

        var comercial = await fixture.CreateDeviceAsync(tenantId, "NB-COMERCIAL");
        await SetTagsAsync(comercial.Id, "comercial");
        await SeedSummaryAsync(tenantId, comercial.Id, hoje, 3600);

        var financeiro = await fixture.CreateDeviceAsync(tenantId, "NB-FINANCEIRO");
        await SetTagsAsync(financeiro.Id, "financeiro");
        await SeedSummaryAsync(tenantId, financeiro.Id, hoje, 7200);

        var range = $"from={hoje:yyyy-MM-dd}&to={hoje:yyyy-MM-dd}";

        // sem tag: soma a organização inteira
        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/dashboard/summary?{range}", token))
        {
            var response = await client.SendAsync(request);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(10800, body.RootElement.GetProperty("totals").GetProperty("seconds_active").GetInt64());
            Assert.Equal(2, body.RootElement.GetProperty("totals").GetProperty("device_count").GetInt32());
        }

        // com tag: só a equipe pedida
        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/dashboard/summary?{range}&tag=comercial", token))
        {
            var response = await client.SendAsync(request);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(3600, body.RootElement.GetProperty("totals").GetProperty("seconds_active").GetInt64());
            Assert.Equal(1, body.RootElement.GetProperty("totals").GetProperty("device_count").GetInt32());
        }

        // etiqueta inexistente: recorte vazio, não 404 (tag não é recurso com dono)
        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/dashboard/summary?{range}&tag=inexistente", token))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(0, body.RootElement.GetProperty("totals").GetProperty("seconds_active").GetInt64());
        }

        // tag vazia equivale a sem filtro (o portal pode mandar o parâmetro sempre)
        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/dashboard/summary?{range}&tag=", token))
        {
            var response = await client.SendAsync(request);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(10800, body.RootElement.GetProperty("totals").GetProperty("seconds_active").GetInt64());
        }
    }

    [Fact]
    public async Task Presence_ETimelineTeam_RespeitamATag()
    {
        var (client, tenantId, token) = await SetupAsync("Org Tag Presença");
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);

        var suporte = await fixture.CreateDeviceAsync(tenantId, "NB-SUPORTE");
        await SetTagsAsync(suporte.Id, "suporte", "noturno");
        var outro = await fixture.CreateDeviceAsync(tenantId, "NB-OUTRO");
        await SetTagsAsync(outro.Id, "financeiro");

        // presença precisa de linha em device_current_state
        foreach (var id in new[] { suporte.Id, outro.Id })
        {
            await TestDb.ExecuteAsync(Cs, """
                INSERT INTO device_current_state (
                    tenant_id, device_id, state, last_contact_at, updated_at)
                VALUES (@t, @d, 'active', now(), now())
                """, ("t", tenantId), ("d", id));
        }

        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, "/api/v1/dashboard/presence?tag=suporte", token))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Single(items);
            Assert.Equal("NB-SUPORTE", items[0].GetProperty("hostname").GetString());
        }

        // timeline de equipe: uma lane por device do recorte (lane vazia também conta)
        using (var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/timeline/team?date={hoje:yyyy-MM-dd}&tag=suporte", token))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var lanes = body.RootElement.GetProperty("lanes").EnumerateArray().ToList();
            Assert.Single(lanes);
            Assert.Equal("NB-SUPORTE", lanes[0].GetProperty("device_name").GetString());
        }
    }

    [Fact]
    public async Task TopApps_ComTag_RecortaRankingEDenominador()
    {
        var (client, tenantId, token) = await SetupAsync("Org Tag Apps");
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);

        var comercial = await fixture.CreateDeviceAsync(tenantId, "NB-APPS-COM");
        await SetTagsAsync(comercial.Id, "comercial");
        var financeiro = await fixture.CreateDeviceAsync(tenantId, "NB-APPS-FIN");
        await SetTagsAsync(financeiro.Id, "financeiro");

        var appExcel = Uuid7.NewUuid7();
        var appChrome = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO app_catalog (id, process_name, display_name, curated)
            VALUES (@e, @en, 'Excel', false), (@c, @cn, 'Chrome', false)
            """,
            ("e", appExcel), ("en", $"excel-{Guid.NewGuid():N}.exe"),
            ("c", appChrome), ("cn", $"chrome-{Guid.NewGuid():N}.exe"));

        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO daily_app_usage (
                tenant_id, summary_date, device_id, device_user_id, app_id, seconds_active, focus_count)
            VALUES (@t, @day, @dcom, @u1, @excel, 3600, 10),
                   (@t, @day, @dfin, @u2, @chrome, 7200, 20)
            """,
            ("t", tenantId), ("day", hoje), ("dcom", comercial.Id), ("dfin", financeiro.Id),
            ("u1", Uuid7.NewUuid7()), ("u2", Uuid7.NewUuid7()), ("excel", appExcel), ("chrome", appChrome));

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/dashboard/top-apps?from={hoje:yyyy-MM-dd}&to={hoje:yyyy-MM-dd}&tag=comercial", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("Excel", items[0].GetProperty("display_name").GetString());

        // o denominador da porcentagem TAMBÉM é recortado (senão a soma passaria de 100%)
        Assert.Equal(3600, body.RootElement.GetProperty("total_seconds_active").GetInt64());
    }
}

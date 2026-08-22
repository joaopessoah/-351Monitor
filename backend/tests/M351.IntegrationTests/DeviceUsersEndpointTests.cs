using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// Endpoints de TITULAR (device_users, Seção 7.4 linha 801). Cobre:
///  - GET /device-users: listagem alfabética do tenant, busca q em windows_username/display_name,
///    filtro por device_id, paginação (page/page_size com teto 100) e a lane-máquina fora;
///  - isolamento de tenant: titular de B nunca aparece na lista de A, GET por id de B → 404,
///    device_id de B → 404 e PATCH em titular de B → 404 (nunca 403 — Princípio 4);
///  - PATCH: AdminPlus edita display_name com trilha update_device_user (de→para), null limpa o
///    apelido, Viewer recebe 403 e nome sem mudança efetiva não gera trilha.
/// </summary>
[Collection(ApiCollection.Name)]
public class DeviceUsersEndpointTests(ApiTestFixture fixture)
{
    private string Conn => fixture.Database.ConnectionString;

    private async Task<(HttpClient Client, Guid TenantId, string AdminToken, string ViewerToken)>
        SetupAsync(string prefix)
    {
        var org = await fixture.CreateOrganizationAsync($"{prefix} {Guid.NewGuid():N}"[..20]);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        return (client, org.Id,
            await AuthClient.LoginAsync(client, admin),
            await AuthClient.LoginAsync(client, viewer));
    }

    /// <summary>Cria um titular (device_user) com SID determinístico.</summary>
    private async Task<Guid> SeedDeviceUserAsync(
        Guid tenantId, Guid deviceId, string windowsUsername, string? displayName)
    {
        var id = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO device_users (
                id, tenant_id, device_id, windows_sid, windows_username, display_name,
                first_seen_at, last_seen_at)
            VALUES (@id, @t, @d, @sid, @wu, @dn, now() - interval '10 days', now())
            """,
            ("id", id), ("t", tenantId), ("d", deviceId),
            ("sid", $"S-1-5-21-DU-{Guid.NewGuid():N}"[..40]),
            ("wu", windowsUsername), ("dn", displayName));
        return id;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string token, object? body = null)
    {
        using var request = AuthClient.AuthorizedRequest(method, url, token, body);
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    // ============================================================ listagem + busca
    [Fact]
    public async Task Lista_OrdemAlfabetica_BuscaEFiltroPorDispositivo()
    {
        var (client, tenantId, adminToken, viewerToken) = await SetupAsync("DuList");
        var nb1 = await fixture.CreateDeviceAsync(tenantId, "NB-DU-1");
        var nb2 = await fixture.CreateDeviceAsync(tenantId, "NB-DU-2");

        var ana = await SeedDeviceUserAsync(tenantId, nb1.Id, "acme\\ana.souza", "Ana Souza");
        var bruno = await SeedDeviceUserAsync(tenantId, nb1.Id, "acme\\bruno.lima", null);
        var carla = await SeedDeviceUserAsync(tenantId, nb2.Id, "acme\\carla.dias", "Carla Dias");

        // Viewer LÊ a listagem (a lista de nomes é insumo de qualquer tela de relatório)
        var list = await SendAsync(client, HttpMethod.Get, "/api/v1/device-users?page_size=100", viewerToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var body = await JsonAsync(list))
        {
            var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
            var ids = items.Select(i => i.GetProperty("id").GetGuid()).ToList();
            Assert.Contains(ana, ids);
            Assert.Contains(bruno, ids);
            Assert.Contains(carla, ids);

            // ordem alfabética pelo NOME EXIBIDO — a chave é COALESCE(display_name,
            // windows_username): o titular sem apelido entra pelo usuário do Windows, então
            // "acme\bruno.lima" vem antes de "Ana Souza", que vem antes de "Carla Dias".
            Assert.True(ids.IndexOf(bruno) < ids.IndexOf(ana));
            Assert.True(ids.IndexOf(ana) < ids.IndexOf(carla));

            // shape do contrato + device_name resolvido
            var anaItem = items.Single(i => i.GetProperty("id").GetGuid() == ana);
            Assert.Equal(nb1.Id, anaItem.GetProperty("device_id").GetGuid());
            Assert.Equal("NB-DU-1", anaItem.GetProperty("device_name").GetString());
            Assert.Equal("acme\\ana.souza", anaItem.GetProperty("windows_username").GetString());
            Assert.Equal("Ana Souza", anaItem.GetProperty("display_name").GetString());
            Assert.NotEqual(JsonValueKind.Null, anaItem.GetProperty("first_seen_at").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, anaItem.GetProperty("last_seen_at").ValueKind);

            // titular sem apelido: display_name null (a tela cai no windows_username)
            var brunoItem = items.Single(i => i.GetProperty("id").GetGuid() == bruno);
            Assert.Equal(JsonValueKind.Null, brunoItem.GetProperty("display_name").ValueKind);
        }

        // busca por display_name
        var byName = await SendAsync(client, HttpMethod.Get, "/api/v1/device-users?q=carla", viewerToken);
        using (var body = await JsonAsync(byName))
        {
            var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(carla, Assert.Single(items).GetProperty("id").GetGuid());
            Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
        }

        // busca por windows_username (case-insensitive, ILIKE) do titular SEM apelido
        var byWindowsUser = await SendAsync(client, HttpMethod.Get, "/api/v1/device-users?q=BRUNO.LIMA", viewerToken);
        using (var body = await JsonAsync(byWindowsUser))
        {
            var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(bruno, Assert.Single(items).GetProperty("id").GetGuid());
        }

        // filtro por dispositivo
        var byDevice = await SendAsync(client, HttpMethod.Get, $"/api/v1/device-users?device_id={nb2.Id}", adminToken);
        using (var body = await JsonAsync(byDevice))
        {
            var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(carla, Assert.Single(items).GetProperty("id").GetGuid());
        }

        // paginação: page_size 1 devolve 1 item e o total do recorte inteiro. No device nb1 a
        // ordem é acme\bruno.lima, Ana Souza — então a página 2 traz a Ana.
        var paged = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/device-users?device_id={nb1.Id}&page=2&page_size=1", adminToken);
        using (var body = await JsonAsync(paged))
        {
            Assert.Equal(ana, Assert.Single(body.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
            Assert.Equal(2, body.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(2, body.RootElement.GetProperty("page").GetInt32());
            Assert.Equal(1, body.RootElement.GetProperty("page_size").GetInt32());
        }

        // GET por id
        var single = await SendAsync(client, HttpMethod.Get, $"/api/v1/device-users/{ana}", viewerToken);
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        using (var body = await JsonAsync(single))
        {
            Assert.Equal("Ana Souza", body.RootElement.GetProperty("display_name").GetString());
        }
    }

    // ============================================================ isolamento de tenant
    [Fact]
    public async Task CrossTenant_ListaVazia_GetEPatch404()
    {
        var (clientA, _, adminA, _) = await SetupAsync("DuIsoA");
        var (_, tenantB, _, _) = await SetupAsync("DuIsoB");
        var deviceB = await fixture.CreateDeviceAsync(tenantB, "NB-DU-ISO-B");
        var anaB = await SeedDeviceUserAsync(tenantB, deviceB.Id, "acme\\ana.b", "Ana de B");

        // a listagem de A não enxerga o titular de B, nem buscando pelo nome dele
        var list = await SendAsync(clientA, HttpMethod.Get, "/api/v1/device-users?q=Ana de B", adminA);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var body = await JsonAsync(list))
        {
            Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(0, body.RootElement.GetProperty("total").GetInt32());
        }

        // GET por id do titular de B → 404 (nunca 403)
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(clientA, HttpMethod.Get, $"/api/v1/device-users/{anaB}", adminA)).StatusCode);

        // filtro por device de B → 404
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(clientA, HttpMethod.Get, $"/api/v1/device-users?device_id={deviceB.Id}", adminA)).StatusCode);

        // PATCH no titular de B → 404 e nada muda
        var patch = await SendAsync(clientA, HttpMethod.Patch, $"/api/v1/device-users/{anaB}", adminA,
            new { display_name = "Renomeado por A" });
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.Equal("Ana de B", await TestDb.ScalarAsync<string>(Conn,
            "SELECT display_name FROM device_users WHERE id = @id", ("id", anaB)));
    }

    // ============================================================ PATCH auditado
    [Fact]
    public async Task Patch_AdminRenomeia_GravaTrilhaDeEPara_NullLimpa_ViewerRecebe403()
    {
        var (client, tenantId, adminToken, viewerToken) = await SetupAsync("DuPatch");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-DU-PATCH");
        var titular = await SeedDeviceUserAsync(tenantId, device.Id, "acme\\jose.silva", null);

        // Viewer não edita
        var viewerPatch = await SendAsync(client, HttpMethod.Patch, $"/api/v1/device-users/{titular}", viewerToken,
            new { display_name = "Tentativa do viewer" });
        Assert.Equal(HttpStatusCode.Forbidden, viewerPatch.StatusCode);
        Assert.Equal(0L, await TestDb.ScalarAsync<long>(Conn,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'update_device_user'", ("t", tenantId)));

        // Admin define o nome amigável
        var patch = await SendAsync(client, HttpMethod.Patch, $"/api/v1/device-users/{titular}", adminToken,
            new { display_name = "José Silva" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using (var body = await JsonAsync(patch))
        {
            Assert.Equal("José Silva", body.RootElement.GetProperty("display_name").GetString());
            Assert.Equal("acme\\jose.silva", body.RootElement.GetProperty("windows_username").GetString());
        }
        Assert.Equal("José Silva", await TestDb.ScalarAsync<string>(Conn,
            "SELECT display_name FROM device_users WHERE id = @id", ("id", titular)));

        // trilha update_device_user com de→para (de: null)
        var audit = await TestDb.RowAsync(Conn, """
            SELECT target_type, target_id, detail::text AS detail
            FROM audit_log WHERE tenant_id = @t AND action = 'update_device_user'
            ORDER BY occurred_at DESC LIMIT 1
            """, ("t", tenantId));
        Assert.NotNull(audit);
        Assert.Equal("device_user", (string)audit!["target_type"]!);
        Assert.Equal(titular, (Guid)audit["target_id"]!);
        using (var detail = JsonDocument.Parse((string)audit["detail"]!))
        {
            var change = detail.RootElement.GetProperty("display_name");
            Assert.Equal(JsonValueKind.Null, change.GetProperty("from").ValueKind);
            Assert.Equal("José Silva", change.GetProperty("to").GetString());
            Assert.Equal(titular, detail.RootElement.GetProperty("device_user_id").GetGuid());
        }

        // PATCH sem mudança efetiva: 200 e NENHUMA trilha nova
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(client, HttpMethod.Patch, $"/api/v1/device-users/{titular}", adminToken,
                new { display_name = "José Silva" })).StatusCode);
        Assert.Equal(1L, await TestDb.ScalarAsync<long>(Conn,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'update_device_user'", ("t", tenantId)));

        // null limpa o apelido (volta a exibir o windows_username) e grava o de→para
        var clear = await SendAsync(client, HttpMethod.Patch, $"/api/v1/device-users/{titular}", adminToken,
            new { display_name = (string?)null });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using (var body = await JsonAsync(clear))
        {
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("display_name").ValueKind);
        }
        Assert.Null(await TestDb.ScalarAsync<string>(Conn,
            "SELECT display_name FROM device_users WHERE id = @id", ("id", titular)));
        Assert.Equal(2L, await TestDb.ScalarAsync<long>(Conn,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'update_device_user'", ("t", tenantId)));
    }
}

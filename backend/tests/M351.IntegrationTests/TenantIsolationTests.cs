using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.Domain.Entities;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// SUITE DE ISOLAMENTO MULTI-TENANT (gate de CI - Secao 11.1 / Principio 4):
/// dois tenants populados; autenticado no tenant A, TODO endpoint do portal com IDs do
/// tenant B responde 404 (nunca 403) e nenhuma listagem vaza recursos de B.
/// </summary>
[Collection(ApiCollection.Name)]
public class TenantIsolationTests(ApiTestFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private string _tokenA = null!;

    private TestUser _ownerA = null!;
    private TestUser _userB = null!;
    private Device _deviceA = null!;
    private Device _deviceB = null!;
    private EnrollmentKey _keyB = null!;

    public async Task InitializeAsync()
    {
        _client = fixture.CreateApiClient();

        var orgA = await fixture.CreateOrganizationAsync("Tenant A");
        var orgB = await fixture.CreateOrganizationAsync("Tenant B");

        // tenant A: owner com MFA (obrigatoria para Owner) e recursos proprios
        _ownerA = await fixture.CreateUserAsync(orgA.Id, UserRole.Owner, mfaEnabled: true);
        _deviceA = await fixture.CreateDeviceAsync(orgA.Id, "NB-TENANT-A");
        await fixture.CreateEnrollmentKeyAsync(orgA.Id, "chave-a");

        // tenant B: usuarios e recursos que JAMAIS podem aparecer para A
        await fixture.CreateUserAsync(orgB.Id, UserRole.Owner, mfaEnabled: true);
        _userB = await fixture.CreateUserAsync(orgB.Id, UserRole.Viewer);
        _deviceB = await fixture.CreateDeviceAsync(orgB.Id, "NB-TENANT-B");
        _keyB = await fixture.CreateEnrollmentKeyAsync(orgB.Id, "chave-b");

        _tokenA = await AuthClient.LoginAsync(_client, _ownerA);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var request = AuthClient.AuthorizedRequest(method, url, _tokenA, body);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task SanidadeRecursoProprio_TenantA_Acessa_SeuDevice()
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/devices/{_deviceA.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetUsuarioDeOutroTenant_Retorna404()
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/users/{_userB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchUsuarioDeOutroTenant_Retorna404()
    {
        var response = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{_userB.Id}", new { role = "admin" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUsuarioDeOutroTenant_Retorna404()
    {
        var response = await SendAsync(HttpMethod.Delete, $"/api/v1/users/{_userB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetDeviceDeOutroTenant_Retorna404()
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/devices/{_deviceB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteEnrollmentKeyDeOutroTenant_Retorna404()
    {
        var response = await SendAsync(HttpMethod.Delete, $"/api/v1/enrollment-keys/{_keyB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListagemDeUsuarios_NaoVazaTenantB()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v1/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var emails = body.RootElement.GetProperty("items").EnumerateArray()
            .Select(u => u.GetProperty("email").GetString())
            .ToList();

        Assert.Contains(_ownerA.Email, emails);
        Assert.DoesNotContain(_userB.Email, emails);
    }

    [Fact]
    public async Task ListagemDeDevices_NaoVazaTenantB()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v1/devices?page_size=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = body.RootElement.GetProperty("items").EnumerateArray()
            .Select(d => d.GetProperty("id").GetGuid())
            .ToList();

        Assert.Contains(_deviceA.Id, ids);
        Assert.DoesNotContain(_deviceB.Id, ids);
    }

    [Fact]
    public async Task ListagemDeEnrollmentKeys_NaoVazaTenantB()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v1/enrollment-keys");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = body.RootElement.GetProperty("items").EnumerateArray()
            .Select(k => k.GetProperty("id").GetGuid())
            .ToList();

        Assert.DoesNotContain(_keyB.Id, ids);
    }

    [Fact]
    public async Task DashboardSummaryComDeviceDeOutroTenant_Retorna404()
    {
        var response = await SendAsync(HttpMethod.Get,
            $"/api/v1/dashboard/summary?from=2026-06-01&to=2026-06-07&device_id={_deviceB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DashboardSummaryComDeviceUserDeOutroTenant_Retorna404_ESemAuditoria()
    {
        // device_user do tenant B semeado direto (o caminho real e a ingestao; o gate aqui
        // e o lookup do filtro device_user_id do summary, segundo parametro portador de ID)
        var deviceUserB = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO device_users (
                id, tenant_id, device_id, windows_sid, windows_username, first_seen_at, last_seen_at)
            VALUES (@id, @t, @d, 'S-1-5-21-ISO-B', 'usuario.b', now(), now())
            """, ("id", deviceUserB), ("t", _deviceB.TenantId), ("d", _deviceB.Id));

        var response = await SendAsync(HttpMethod.Get,
            $"/api/v1/dashboard/summary?from=2026-06-01&to=2026-06-07&device_user_id={deviceUserB}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // o probe que levou 404 nao pode deixar rastro de view_report (nao houve acesso a dado)
        var audits = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE action = 'view_report' AND target_id = @id",
            ("id", deviceUserB));
        Assert.Equal(0L, audits);
    }

    [Fact]
    public async Task DashboardSummaryETopApps_NaoVazamAgregadosDoTenantB()
    {
        // semeia agregados diários DIRETO no tenant B: o gate aqui é da LEITURA dos
        // endpoints F3.2, não do pipeline (coberto em DailyAggregationTests)
        var appId = Uuid7.NewUuid7();
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO app_catalog (id, process_name, display_name)
            VALUES (@a, 'iso-leak.exe', 'iso-leak.exe')
            ON CONFLICT (process_name) DO NOTHING
            """, ("a", appId));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO daily_device_summaries (
                tenant_id, summary_date, device_id, device_user_id,
                seconds_active, seconds_on, computed_at)
            VALUES (@t, '2026-06-01', @d, '00000000-0000-0000-0000-000000000000', 3600, 3600, now())
            """, ("t", _deviceB.TenantId), ("d", _deviceB.Id));
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO daily_app_usage (
                tenant_id, summary_date, device_id, device_user_id, app_id, seconds_active, focus_count)
            SELECT @t, '2026-06-01', @d, '00000000-0000-0000-0000-000000000000', a.id, 3600, 3
            FROM app_catalog a WHERE a.process_name = 'iso-leak.exe'
            """, ("t", _deviceB.TenantId), ("d", _deviceB.Id));

        var summary = await SendAsync(HttpMethod.Get, "/api/v1/dashboard/summary?from=2026-05-30&to=2026-06-03");
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        using (var body = JsonDocument.Parse(await summary.Content.ReadAsStringAsync()))
        {
            Assert.Empty(body.RootElement.GetProperty("days").EnumerateArray());
            Assert.Equal(0, body.RootElement.GetProperty("totals").GetProperty("device_count").GetInt32());
        }

        var topApps = await SendAsync(HttpMethod.Get, "/api/v1/dashboard/top-apps?from=2026-05-30&to=2026-06-03");
        Assert.Equal(HttpStatusCode.OK, topApps.StatusCode);
        using (var body = JsonDocument.Parse(await topApps.Content.ReadAsStringAsync()))
        {
            Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(0, body.RootElement.GetProperty("total_seconds_active").GetInt64());
        }
    }

    [Fact]
    public async Task TimelineTeam_NaoVazaLanesDoTenantB()
    {
        // o modo equipe lista TODOS os devices do tenant — endpoint sem ID na URL, o gate
        // aqui e a listagem nao vazar lanes (nem vazias) de devices do tenant B
        var response = await SendAsync(HttpMethod.Get, "/api/v1/timeline/team?date=2026-06-01");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = body.RootElement.GetProperty("lanes").EnumerateArray()
            .Select(l => l.GetProperty("device_id").GetGuid())
            .ToList();

        Assert.Contains(_deviceA.Id, ids);
        Assert.DoesNotContain(_deviceB.Id, ids);
    }

    [Fact]
    public async Task RespostaCruzada_NuncaEh403_SempreEh404()
    {
        // a distincao importa: 403 confirmaria a existencia do recurso de outro tenant
        var alvos = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Get, $"/api/v1/users/{_userB.Id}"),
            (HttpMethod.Get, $"/api/v1/devices/{_deviceB.Id}"),
            (HttpMethod.Delete, $"/api/v1/enrollment-keys/{_keyB.Id}"),
        };

        foreach (var (method, url) in alvos)
        {
            var response = await SendAsync(method, url);
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}

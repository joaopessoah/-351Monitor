using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// Autenticação de device (scheme separado do JWT do portal) e isolamento multi-tenant das
/// rotas de agente (extensão da suíte 11.1.1): token revogado/desconhecido → 401; ingestão
/// com token do tenant B JAMAIS grava em A; device token não autentica rotas do portal e
/// JWT do portal não autentica a ingestão.
/// </summary>
[Collection(ApiCollection.Name)]
public class DeviceAuthTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    [Fact]
    public async Task TokenDesconhecido_Retorna401()
    {
        var client = fixture.CreateApiClient();
        var response = await AgentClient.SendBatchAsync(client, "dt_token-que-nao-existe", []);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SemAuthorization_Retorna401()
    {
        var client = fixture.CreateApiClient();
        var response = await client.PostAsJsonAsync("/api/v1/ingest/batch",
            new Dictionary<string, object?> { ["events"] = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenRevogadoPeloPortal_Retorna401()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org Revoga Token");
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var device = await AgentClient.EnrollAsync(client, fullKey);

        // sanidade: token funciona antes da revogação
        var ok = await AgentClient.SendBatchAsync(client, device.DeviceToken, []);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var adminToken = await AuthClient.LoginAsync(client, admin);
        using var revoke = AuthClient.AuthorizedRequest(
            HttpMethod.Post, $"/api/v1/devices/{device.DeviceId}/revoke", adminToken);
        var revokeResponse = await client.SendAsync(revoke);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var after = await AgentClient.SendBatchAsync(client, device.DeviceToken, []);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task JwtDoPortal_NaoAutenticaIngestao()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org Jwt vs Ingest");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var accessToken = await AuthClient.LoginAsync(client, owner);

        var response = await AgentClient.SendBatchAsync(client, accessToken, []);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeviceToken_NaoAutenticaRotasDoPortal()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org Device vs Portal");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var device = await AgentClient.EnrollAsync(client, fullKey);

        foreach (var url in new[] { "/api/v1/devices", "/api/v1/me", "/api/v1/dashboard/presence" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", device.DeviceToken);
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task IngestaoComTokenDoTenantB_GravaSomenteEmB_NuncaEmA()
    {
        var client = fixture.CreateApiClient();
        var orgA = await fixture.CreateOrganizationAsync("Org Iso A");
        var orgB = await fixture.CreateOrganizationAsync("Org Iso B");
        var (_, keyB) = await fixture.CreateEnrollmentKeyWithSecretAsync(orgB.Id);
        var deviceB = await AgentClient.EnrollAsync(client, keyB);

        var factory = new EventFactory();
        var response = await AgentClient.SendBatchAsync(client, deviceB.DeviceToken,
            [factory.Event("HEARTBEAT", data: new Dictionary<string, object?> { ["state"] = "active" })]);
        using var ack = await AgentClient.ReadAckAsync(response);
        Assert.Equal(1, ack.RootElement.GetProperty("accepted").GetInt32());

        var inB = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM raw_events WHERE tenant_id = @t", ("t", orgB.Id));
        var inA = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM raw_events WHERE tenant_id = @t", ("t", orgA.Id));
        Assert.Equal(1, inB);
        Assert.Equal(0, inA);

        // a presença do tenant A não vê o device de B (e vice-versa o estado fica em B)
        var ownerA = await fixture.CreateUserAsync(orgA.Id, UserRole.Owner, mfaEnabled: true);
        var tokenA = await AuthClient.LoginAsync(client, ownerA);
        using var presenceRequest = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/dashboard/presence", tokenA);
        var presenceResponse = await client.SendAsync(presenceRequest);
        Assert.Equal(HttpStatusCode.OK, presenceResponse.StatusCode);

        using var presence = JsonDocument.Parse(await presenceResponse.Content.ReadAsStringAsync());
        var ids = presence.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("device_id").GetGuid());
        Assert.DoesNotContain(deviceB.DeviceId, ids);
    }

    [Fact]
    public async Task RevogarDeviceDeOutroTenant_Retorna404_SemEnfileirarComando()
    {
        var client = fixture.CreateApiClient();
        var orgA = await fixture.CreateOrganizationAsync("Org Revoke A");
        var orgB = await fixture.CreateOrganizationAsync("Org Revoke B");
        var adminA = await fixture.CreateUserAsync(orgA.Id, UserRole.Admin, mfaEnabled: true);
        var deviceB = await fixture.CreateDeviceAsync(orgB.Id, "NB-DE-B");

        var tokenA = await AuthClient.LoginAsync(client, adminA);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Delete })
        {
            var url = method == HttpMethod.Post
                ? $"/api/v1/devices/{deviceB.Id}/revoke"
                : $"/api/v1/devices/{deviceB.Id}";
            using var request = AuthClient.AuthorizedRequest(method, url, tokenA);
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var commands = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM device_commands WHERE device_id = @d", ("d", deviceB.Id));
        Assert.Equal(0, commands);
    }
}

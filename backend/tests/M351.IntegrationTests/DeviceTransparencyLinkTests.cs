using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// ENTREGA do link de transparência por token (a página /public/t/{token} em si é coberta por
/// ComplianceCenterTests). Sem esta entrega o token existia só no banco e ninguém chegava na
/// página: o agente montava a url por slug e nenhuma tela do portal mostrava o token.
///
/// Dois caminhos, ambos já existentes, nenhum canal novo:
///  - agente: campo device_transparency_url do objeto config (resposta do enroll e config
///    reentregue no ack), com o tray caindo na url por slug quando ele não vem;
///  - portal: GET /devices/{id}/transparency-link, restrito a Admin+ — o token é um segredo de
///    baixo valor mas é um segredo, e Viewer não o vê.
/// </summary>
[Collection(ApiCollection.Name)]
public class DeviceTransparencyLinkTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    /// <summary>Portal:BaseUrl do fixture de teste (ApiTestFixture.UseSetting).</summary>
    private const string PortalBaseUrl = "http://localhost:5173";

    // ============================================================ entrega ao agente (config 5.5)

    [Fact]
    public async Task Enroll_EntregaAUrlDeTransparenciaDaquiloDispositivo()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org Link Enroll");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);

        var response = await AgentClient.EnrollRawAsync(client, fullKey, AgentClient.NewFingerprint(), "NB-LINK");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var deviceId = root.GetProperty("device_id").GetGuid();

        var token = await TestDb.ScalarAsync<Guid>(Cs,
            "SELECT transparency_token FROM devices WHERE id = @id", ("id", deviceId));
        Assert.NotEqual(Guid.Empty, token);

        var config = root.GetProperty("config");
        Assert.Equal($"{PortalBaseUrl}/t/{token}", config.GetProperty("device_transparency_url").GetString());
        // a url por slug continua existindo: é o fallback do agente antigo e o link divulgável
        Assert.Contains($"/transparencia/{org.Slug}", config.GetProperty("transparency_url").GetString());
    }

    /// <summary>
    /// É por aqui que um agente JÁ enrolado (de antes do campo existir) recebe o token: o ack
    /// reentrega a config inteira quando o config_version diverge.
    /// </summary>
    [Fact]
    public async Task ConfigDoAck_ReentregaAUrlPorToken()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org Link Ack");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var device = await AgentClient.EnrollAsync(client, fullKey);

        // bump da config no banco: o próximo ack devolve o objeto config completo
        await TestDb.ExecuteAsync(Cs,
            "UPDATE tenant_agent_configs SET config_version = config_version + 1 WHERE tenant_id = @t",
            ("t", org.Id));

        var response = await AgentClient.SendBatchAsync(
            client, device.DeviceToken, [], configVersion: device.ConfigVersion);
        using var ack = await AgentClient.ReadAckAsync(response);

        var token = await TestDb.ScalarAsync<Guid>(Cs,
            "SELECT transparency_token FROM devices WHERE id = @id", ("id", device.DeviceId));

        var config = ack.RootElement.GetProperty("config");
        Assert.Equal(JsonValueKind.Object, config.ValueKind);
        Assert.Equal($"{PortalBaseUrl}/t/{token}", config.GetProperty("device_transparency_url").GetString());
    }

    /// <summary>
    /// Device SEM token (linha anterior ao backfill que nunca re-enrollou): o campo vem null e o
    /// agente cai na url por slug. É o fallback que mantém agente antigo e device antigo vivos.
    /// </summary>
    [Fact]
    public async Task ConfigDoAck_SemToken_MandaNullEOAgenteCaiNoSlug()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org Link Sem Token");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var device = await AgentClient.EnrollAsync(client, fullKey);

        await TestDb.ExecuteAsync(Cs,
            "UPDATE devices SET transparency_token = NULL WHERE id = @id", ("id", device.DeviceId));
        await TestDb.ExecuteAsync(Cs,
            "UPDATE tenant_agent_configs SET config_version = config_version + 1 WHERE tenant_id = @t",
            ("t", org.Id));

        var response = await AgentClient.SendBatchAsync(
            client, device.DeviceToken, [], configVersion: device.ConfigVersion);
        using var ack = await AgentClient.ReadAckAsync(response);

        var config = ack.RootElement.GetProperty("config");
        Assert.Equal(JsonValueKind.Null, config.GetProperty("device_transparency_url").ValueKind);
        Assert.Contains($"/transparencia/{org.Slug}", config.GetProperty("transparency_url").GetString());
    }

    // ============================================================ entrega ao portal (Admin+)

    [Fact]
    public async Task TransparencyLink_Admin_DevolveAUrlQueAbreAPaginaPublica()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org Link Portal");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var device = await AgentClient.EnrollAsync(client, fullKey);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var adminToken = await AuthClient.LoginAsync(client, admin);

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/devices/{device.DeviceId}/transparency-link", adminToken);
        var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, payload);

        using var body = JsonDocument.Parse(payload);
        Assert.Equal(device.DeviceId, body.RootElement.GetProperty("device_id").GetGuid());
        var url = body.RootElement.GetProperty("url").GetString()!;

        var token = await TestDb.ScalarAsync<Guid>(Cs,
            "SELECT transparency_token FROM devices WHERE id = @id", ("id", device.DeviceId));
        Assert.Equal($"{PortalBaseUrl}/t/{token}", url);

        // ponta a ponta: a url oferecida ao gestor é MESMO a que abre a página pública
        var publica = await client.GetAsync($"/api/v1/public/t/{token}");
        Assert.Equal(HttpStatusCode.OK, publica.StatusCode);
    }

    /// <summary>Viewer NÃO vê o token: papel de leitura não recebe segredo (nem 404, é 403).</summary>
    [Fact]
    public async Task TransparencyLink_Viewer_Recebe403()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org Link Viewer");
        var device = await fixture.CreateDeviceAsync(org.Id, "NB-VIEWER");
        await TestDb.ExecuteAsync(Cs,
            "UPDATE devices SET transparency_token = @tok WHERE id = @id",
            ("tok", Guid.NewGuid()), ("id", device.Id));

        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var viewerToken = await AuthClient.LoginAsync(client, viewer);

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/devices/{device.Id}/transparency-link", viewerToken);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Device de OUTRO tenant é 404, nunca 403: a resposta não confirma existência.</summary>
    [Fact]
    public async Task TransparencyLink_DeviceDeOutroTenant_Retorna404()
    {
        var client = fixture.CreateApiClient();
        var orgA = await fixture.CreateOrganizationAsync("Org Link Tenant A");
        var orgB = await fixture.CreateOrganizationAsync("Org Link Tenant B");
        var deviceB = await fixture.CreateDeviceAsync(orgB.Id, "NB-OUTRO-TENANT");
        await TestDb.ExecuteAsync(Cs,
            "UPDATE devices SET transparency_token = @tok WHERE id = @id",
            ("tok", Guid.NewGuid()), ("id", deviceB.Id));

        var adminA = await fixture.CreateUserAsync(orgA.Id, UserRole.Admin, mfaEnabled: true);
        var tokenA = await AuthClient.LoginAsync(client, adminA);

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/devices/{deviceB.Id}/transparency-link", tokenA);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Device sem token: 404, o mesmo do inexistente — não há link a oferecer.</summary>
    [Fact]
    public async Task TransparencyLink_DeviceSemToken_Retorna404()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org Link Sem Token Portal");
        var device = await fixture.CreateDeviceAsync(org.Id, "NB-SEM-TOKEN");
        await TestDb.ExecuteAsync(Cs,
            "UPDATE devices SET transparency_token = NULL WHERE id = @id", ("id", device.Id));

        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var adminToken = await AuthClient.LoginAsync(client, admin);

        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/devices/{device.Id}/transparency-link", adminToken);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TransparencyLink_SemAutenticacao_Retorna401()
    {
        var client = fixture.CreateApiClient();
        var response = await client.GetAsync($"/api/v1/devices/{Guid.NewGuid()}/transparency-link");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

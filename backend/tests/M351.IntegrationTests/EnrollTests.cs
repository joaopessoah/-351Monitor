using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// POST /api/v1/agent/enroll (Seção 5.7): key válida (201 + dt_ + config canônica),
/// key revogada/desconhecida/esgotada (403), device_limit do plano (422) e
/// re-enroll idempotente pela machine_fingerprint (mesmo device_id, token novo).
/// </summary>
[Collection(ApiCollection.Name)]
public class EnrollTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    [Fact]
    public async Task EnrollComKeyValida_Retorna201ComTokenEConfigCanonica()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org Enroll Ok");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);

        var response = await AgentClient.EnrollRawAsync(client, fullKey, AgentClient.NewFingerprint(), "NB-JOAO");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.True(root.GetProperty("device_id").GetGuid() != Guid.Empty);
        Assert.StartsWith("dt_", root.GetProperty("device_token").GetString());
        Assert.Equal(1, root.GetProperty("config_version").GetInt32());

        // objeto config completo — 8 campos, números canônicos N1/N2/N4 e defaults de fábrica
        var config = root.GetProperty("config");
        Assert.Equal(60, config.GetProperty("heartbeat_sec").GetInt32());
        Assert.Equal(5, config.GetProperty("active_window_poll_sec").GetInt32());
        Assert.Equal(300, config.GetProperty("idle_threshold_sec").GetInt32());
        Assert.Equal("MASKED_PATTERNS", config.GetProperty("window_title_policy").GetString());
        Assert.True(config.GetProperty("masked_patterns").GetArrayLength() > 0);
        Assert.Contains("keepass.exe",
            config.GetProperty("ignored_processes").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("ALWAYS", config.GetProperty("collection_window").GetProperty("mode").GetString());
        Assert.Contains($"/transparencia/{org.Slug}", config.GetProperty("transparency_url").GetString());

        // o token fica APENAS como hash no banco (nunca em claro)
        var deviceId = root.GetProperty("device_id").GetGuid();
        var hostname = await TestDb.ScalarAsync<string>(Cs,
            "SELECT hostname FROM devices WHERE id = @id", ("id", deviceId));
        Assert.Equal("NB-JOAO", hostname);
    }

    [Fact]
    public async Task EnrollComKeyRevogada_Retorna403()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org Key Revogada");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(
            org.Id, revokedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var response = await AgentClient.EnrollRawAsync(client, fullKey, AgentClient.NewFingerprint());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EnrollComKeyDesconhecida_Retorna403()
    {
        var client = fixture.CreateApiClient();
        var response = await AgentClient.EnrollRawAsync(client, "ek_000000000000", AgentClient.NewFingerprint());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EnrollComKeyExpirada_Retorna403()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org Key Expirada");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(
            org.Id, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var response = await AgentClient.EnrollRawAsync(client, fullKey, AgentClient.NewFingerprint());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EnrollAlemDoDeviceLimit_Retorna422()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org Limite 1", deviceLimit: 1);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);

        await AgentClient.EnrollAsync(client, fullKey); // 1º device ocupa o limite

        var response = await AgentClient.EnrollRawAsync(client, fullKey, AgentClient.NewFingerprint());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("device_limit_exceeded", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReEnrollMesmaFingerprint_PreservaDevice_RevogaTokenAntigo()
    {
        var client = fixture.CreateApiClient();
        // limite 1: o re-enroll NÃO pode contar como device novo
        var org = await fixture.CreateOrganizationAsync("Org ReEnroll", deviceLimit: 1);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var fingerprint = AgentClient.NewFingerprint();

        var first = await AgentClient.EnrollAsync(client, fullKey, fingerprint);
        var second = await AgentClient.EnrollAsync(client, fullKey, fingerprint);

        Assert.Equal(first.DeviceId, second.DeviceId);           // mesmo device, histórico preservado
        Assert.NotEqual(first.DeviceToken, second.DeviceToken);  // token novo

        var count = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM devices WHERE tenant_id = @t", ("t", org.Id));
        Assert.Equal(1, count);

        // token antigo foi revogado → 401; token novo funciona
        var oldTokenResponse = await AgentClient.SendBatchAsync(client, first.DeviceToken, []);
        Assert.Equal(HttpStatusCode.Unauthorized, oldTokenResponse.StatusCode);

        var newTokenResponse = await AgentClient.SendBatchAsync(client, second.DeviceToken, []);
        Assert.Equal(HttpStatusCode.OK, newTokenResponse.StatusCode);
    }

    [Fact]
    public async Task EnrollComMaxUsesEsgotado_Retorna403()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org MaxUses");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id, maxUses: 1);

        await AgentClient.EnrollAsync(client, fullKey); // consome o único uso

        var response = await AgentClient.EnrollRawAsync(client, fullKey, AgentClient.NewFingerprint());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EnrollSemCamposObrigatorios_Retorna400()
    {
        var client = fixture.CreateApiClient();
        var response = await client.PostAsJsonAsync("/api/v1/agent/enroll",
            new Dictionary<string, object?> { ["hostname"] = "NB-SEM-KEY" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

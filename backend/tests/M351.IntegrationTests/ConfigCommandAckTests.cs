using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// O ack do batch é o ÚNICO canal de config e comandos (Seção 5.5): config entregue quando o
/// config_version do agente está desatualizado (senão null); POLICY_APPLIED registra a versão
/// aplicada no device; UNENROLL enfileirado na revogação é entregue no próximo ack (após o
/// re-enroll do agente) e marcado como entregue.
/// </summary>
[Collection(ApiCollection.Name)]
public class ConfigCommandAckTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private async Task<(HttpClient Client, EnrolledDevice Device, Guid TenantId, string FullKey)> SetupAsync(string orgName)
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync(orgName);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var device = await AgentClient.EnrollAsync(client, fullKey);
        return (client, device, org.Id, fullKey);
    }

    [Fact]
    public async Task ConfigVersionIgual_AckTrazConfigNull()
    {
        var (client, device, _, _) = await SetupAsync("Org Config Atual");

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken, [], configVersion: device.ConfigVersion);
        using var ack = await AgentClient.ReadAckAsync(response);

        Assert.Equal(device.ConfigVersion, ack.RootElement.GetProperty("config_version").GetInt32());
        Assert.Equal(JsonValueKind.Null, ack.RootElement.GetProperty("config").ValueKind);
    }

    [Fact]
    public async Task ConfigDesatualizada_AckEntregaConfigCompleta()
    {
        var (client, device, tenantId, _) = await SetupAsync("Org Config Bump");

        // bump no banco (ex.: tela de privacidade do portal): idle 600 s + nova versão
        await TestDb.ExecuteAsync(Cs,
            "UPDATE tenant_agent_configs SET config_version = config_version + 1, idle_threshold_sec = 600 WHERE tenant_id = @t",
            ("t", tenantId));

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken, [], configVersion: device.ConfigVersion);
        using var ack = await AgentClient.ReadAckAsync(response);

        Assert.Equal(device.ConfigVersion + 1, ack.RootElement.GetProperty("config_version").GetInt32());

        var config = ack.RootElement.GetProperty("config");
        Assert.Equal(JsonValueKind.Object, config.ValueKind);
        Assert.Equal(600, config.GetProperty("idle_threshold_sec").GetInt32());

        // objeto completo: os 8 campos sempre presentes (Seção 5.5)
        foreach (var field in new[]
                 {
                     "heartbeat_sec", "active_window_poll_sec", "idle_threshold_sec", "window_title_policy",
                     "masked_patterns", "ignored_processes", "collection_window", "transparency_url",
                 })
        {
            Assert.True(config.TryGetProperty(field, out _), $"config sem o campo {field}");
        }
    }

    [Fact]
    public async Task PolicyApplied_RegistraConfigVersionNoDevice()
    {
        var (client, device, tenantId, _) = await SetupAsync("Org Policy Applied");

        await TestDb.ExecuteAsync(Cs,
            "UPDATE tenant_agent_configs SET config_version = 7 WHERE tenant_id = @t", ("t", tenantId));

        var factory = new EventFactory();
        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken,
            [factory.Event("POLICY_APPLIED", data: new Dictionary<string, object?> { ["config_version"] = 7 },
                windowsSid: null, windowsUser: null, sessionId: null)]);
        using var ack = await AgentClient.ReadAckAsync(response);
        Assert.Equal(1, ack.RootElement.GetProperty("accepted").GetInt32());

        var applied = await TestDb.ScalarAsync<int>(Cs,
            "SELECT config_version FROM devices WHERE id = @d", ("d", device.DeviceId));
        Assert.Equal(7, applied);
    }

    [Fact]
    public async Task Unenroll_EntregueNoAckAposRevogacao_EMarcadoComoEntregue()
    {
        var (client, device, tenantId, fullKey) = await SetupAsync("Org Unenroll");
        var admin = await fixture.CreateUserAsync(tenantId, UserRole.Admin, mfaEnabled: true);
        var adminToken = await AuthClient.LoginAsync(client, admin);

        // revogação pelo portal: token cai (401) e UNENROLL fica enfileirado
        using var revoke = AuthClient.AuthorizedRequest(
            HttpMethod.Delete, $"/api/v1/devices/{device.DeviceId}", adminToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(revoke)).StatusCode);

        var with401 = await AgentClient.SendBatchAsync(client, device.DeviceToken, []);
        Assert.Equal(HttpStatusCode.Unauthorized, with401.StatusCode);

        // N15: o agente re-enrolla com a key persistida — mesma fingerprint, novo token
        var fingerprint = await TestDb.ScalarAsync<string>(Cs,
            "SELECT machine_fingerprint FROM devices WHERE id = @d", ("d", device.DeviceId));
        var reEnrolled = await AgentClient.EnrollAsync(client, fullKey, fingerprint);
        Assert.Equal(device.DeviceId, reEnrolled.DeviceId);

        // o próximo ack entrega o UNENROLL pendente
        using var ack1 = await AgentClient.ReadAckAsync(
            await AgentClient.SendBatchAsync(client, reEnrolled.DeviceToken, []));
        var commands = ack1.RootElement.GetProperty("commands");
        Assert.Equal(1, commands.GetArrayLength());
        var command = commands.EnumerateArray().Single();
        Assert.Equal("UNENROLL", command.GetProperty("type").GetString());
        Assert.True(command.GetProperty("id").GetGuid() != Guid.Empty);
        Assert.Equal(JsonValueKind.Object, command.GetProperty("payload").ValueKind);

        var deliveredAt = await TestDb.ScalarAsync<DateTime?>(Cs,
            "SELECT delivered_at FROM device_commands WHERE device_id = @d", ("d", device.DeviceId));
        Assert.NotNull(deliveredAt);

        // entrega marcada: o comando não se repete no ack seguinte
        using var ack2 = await AgentClient.ReadAckAsync(
            await AgentClient.SendBatchAsync(client, reEnrolled.DeviceToken, []));
        Assert.Equal(0, ack2.RootElement.GetProperty("commands").GetArrayLength());
    }
}

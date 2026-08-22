using System.Net.Http.Headers;
using System.Net.Http.Json;
using M351.Domain;
using M351.Infrastructure.Security;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// Enforcement do status 'paused' (F5): device pausado pelo gestor NÃO persiste coleta (a
/// ingestão aceita o lote para o agente drenar a fila, mas descarta), a presença é zerada na
/// hora do PATCH, e o DSR delete não deixa resquício do titular em device_current_state.
/// </summary>
[Collection(ApiCollection.Name)]
public class DevicePauseTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private async Task<(HttpClient Client, EnrolledDevice Device, Guid TenantId, string AdminToken)>
        SetupAsync(string orgName)
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync(orgName);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var device = await AgentClient.EnrollAsync(client, fullKey);
        return (client, device, org.Id, await AuthClient.LoginAsync(client, admin));
    }

    private static async Task<HttpResponseMessage> PatchStatusAsync(
        HttpClient client, string token, Guid deviceId, string status)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/devices/{deviceId}")
        {
            Content = JsonContent.Create(new { status }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task DevicePausado_IngestaoAceitaMasDescarta_EPresencaEZerada()
    {
        var (client, device, tenantId, adminToken) = await SetupAsync("Org Pausa");
        var factory = new EventFactory();
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);

        // 1) batch normal: projeta presença e persiste raw_events
        var first = await AgentClient.SendBatchAsync(client, device.DeviceToken,
        [
            factory.Event("SESSION_START", at, new Dictionary<string, object?> { ["logon_type"] = "console" }),
            factory.Event("ACTIVE_WINDOW_CHANGED", at.AddSeconds(10), new Dictionary<string, object?>
            {
                ["process_name"] = "EXCEL.EXE",
                ["window_title"] = "Orcamento_2026.xlsx - Excel",
                ["title_masked"] = false,
            }),
        ]);
        using (var ack = await AgentClient.ReadAckAsync(first))
        {
            Assert.Equal(2, ack.RootElement.GetProperty("accepted").GetInt32());
        }

        var rawAntes = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM raw_events WHERE device_id = @d", ("d", device.DeviceId));
        Assert.Equal(2, rawAntes);

        // 2) gestor pausa: a presença é zerada NA HORA (sem esperar o próximo batch)
        var patch = await PatchStatusAsync(client, adminToken, device.DeviceId, "paused");
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        var state = await TestDb.RowAsync(Cs,
            "SELECT state, windows_sid, windows_username, foreground_process, foreground_title FROM device_current_state WHERE device_id = @d",
            ("d", device.DeviceId));
        Assert.NotNull(state);
        Assert.Equal("no_data", (string)state!["state"]!);
        Assert.Null(state["windows_sid"]);
        Assert.Null(state["windows_username"]);
        Assert.Null(state["foreground_title"]);

        // 3) batch com device pausado: ack ACEITA (o agente drena a fila) mas NADA é persistido
        var paused = await AgentClient.SendBatchAsync(client, device.DeviceToken,
        [
            factory.Event("ACTIVE_WINDOW_CHANGED", at.AddMinutes(1), new Dictionary<string, object?>
            {
                ["process_name"] = "chrome.exe",
                ["window_title"] = "Assunto pessoal - Google Chrome",
                ["title_masked"] = false,
            }),
            factory.Event("HEARTBEAT", at.AddMinutes(2), new Dictionary<string, object?>
            {
                ["state"] = "active",
                ["idle_ms"] = 0,
            }),
        ]);
        using (var ack = await AgentClient.ReadAckAsync(paused))
        {
            Assert.Equal(2, ack.RootElement.GetProperty("accepted").GetInt32());
            Assert.Equal(0, ack.RootElement.GetProperty("duplicates").GetInt32());
        }

        var rawDepois = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM raw_events WHERE device_id = @d", ("d", device.DeviceId));
        Assert.Equal(2, rawDepois); // nada novo persistido

        var stateDepois = await TestDb.RowAsync(Cs,
            "SELECT state, foreground_title FROM device_current_state WHERE device_id = @d",
            ("d", device.DeviceId));
        Assert.Equal("no_data", (string)stateDepois!["state"]!);
        Assert.Null(stateDepois["foreground_title"]);

        // last_seen_at continua avançando: pausa não é agente morto (saúde operacional preservada)
        var lastSeen = await TestDb.ScalarAsync<DateTime?>(Cs,
            "SELECT last_seen_at FROM devices WHERE id = @d", ("d", device.DeviceId));
        Assert.NotNull(lastSeen);
        Assert.True(lastSeen!.Value.ToUniversalTime() > DateTime.UtcNow.AddMinutes(-1),
            $"last_seen_at não avançou: {lastSeen:o}");

        // 4) reativado: a coleta volta a persistir
        var reativa = await PatchStatusAsync(client, adminToken, device.DeviceId, "active");
        Assert.True(reativa.IsSuccessStatusCode);

        var resumed = await AgentClient.SendBatchAsync(client, device.DeviceToken,
        [
            factory.Event("ACTIVE_WINDOW_CHANGED", at.AddMinutes(3), new Dictionary<string, object?>
            {
                ["process_name"] = "winword.exe",
                ["window_title"] = "Relatorio.docx - Word",
                ["title_masked"] = false,
            }),
        ]);
        using (var ack = await AgentClient.ReadAckAsync(resumed))
        {
            Assert.Equal(1, ack.RootElement.GetProperty("accepted").GetInt32());
        }

        var rawFinal = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM raw_events WHERE device_id = @d", ("d", device.DeviceId));
        Assert.Equal(3, rawFinal);
    }

    [Fact]
    public async Task DsrDelete_LimpaResquicioDoTitularEmDeviceCurrentState()
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org DSR Presença");
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var ownerToken = await AuthClient.LoginAsync(client, owner);
        var device = await fixture.CreateDeviceAsync(org.Id, "NB-DSR-PRESENCA");

        // titular + projeção de presença apontando para ele (como após o último evento do dia)
        var subjectId = Uuid7.NewUuid7();
        const string sid = "S-1-5-21-DSR-PRESENCA-0001";
        const string username = "acme\\pessoa.excluida";
        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO device_users (
                id, tenant_id, device_id, windows_sid, windows_username, display_name, first_seen_at, last_seen_at)
            VALUES (@id, @t, @d, @sid, @wu, 'Pessoa Excluída', now(), now())
            """,
            ("id", subjectId), ("t", org.Id), ("d", device.Id), ("sid", sid), ("wu", username));
        await TestDb.ExecuteAsync(Cs, """
            INSERT INTO device_current_state (
                tenant_id, device_id, state, windows_sid, windows_username,
                foreground_process, foreground_title, last_contact_at, updated_at)
            VALUES (@t, @d, 'active', @sid, @wu, 'chrome.exe', 'Prontuário - Maria Silva.pdf', now(), now())
            """,
            ("t", org.Id), ("d", device.Id), ("sid", sid), ("wu", username));

        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"/api/v1/privacy/subjects/{subjectId}/data")
        {
            Content = JsonContent.Create(new
            {
                confirmation = username,
                reason = "Solicitacao de exclusao do titular (art. 18 V)",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        // nenhum resquício do titular na projeção de presença
        var state = await TestDb.RowAsync(Cs,
            "SELECT windows_sid, windows_username, foreground_process, foreground_title FROM device_current_state WHERE device_id = @d",
            ("d", device.Id));
        Assert.NotNull(state);
        Assert.Null(state!["windows_sid"]);
        Assert.Null(state["windows_username"]);
        Assert.Null(state["foreground_process"]);
        Assert.Null(state["foreground_title"]);
    }
}

using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// device_current_state atualizado NO CAMINHO DA INGESTÃO (Seção 7.1): estado, app em foco e
/// janela derivados de HEARTBEAT/ACTIVE_WINDOW_CHANGED/IDLE_*/LOCK/UNLOCK; device_users
/// registrados; GET /api/v1/dashboard/presence (Seção 7.4) com a regra N6.
/// </summary>
[Collection(ApiCollection.Name)]
public class CurrentStateTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    private async Task<(HttpClient Client, EnrolledDevice Device, Guid TenantId)> SetupAsync(string orgName)
    {
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync(orgName);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var device = await AgentClient.EnrollAsync(client, fullKey);
        return (client, device, org.Id);
    }

    [Fact]
    public async Task ActiveWindowChanged_ProjetaEstadoAtivoComAppEJanela()
    {
        var (client, device, tenantId) = await SetupAsync("Org Estado Ativo");
        var factory = new EventFactory();
        var at = DateTimeOffset.UtcNow.AddMinutes(-2);

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken,
        [
            factory.Event("SESSION_START", at, new Dictionary<string, object?> { ["logon_type"] = "console" }),
            factory.Event("ACTIVE_WINDOW_CHANGED", at.AddSeconds(10), new Dictionary<string, object?>
            {
                ["process_name"] = "EXCEL.EXE", // o servidor normaliza para lowercase
                ["window_title"] = "Orcamento_2026.xlsx - Excel",
                ["title_masked"] = false,
            }),
        ]);
        using var _ = await AgentClient.ReadAckAsync(response);

        var row = await TestDb.RowAsync(Cs,
            "SELECT state, windows_sid, windows_username, foreground_process, foreground_title FROM device_current_state WHERE device_id = @d AND tenant_id = @t",
            ("d", device.DeviceId), ("t", tenantId));
        Assert.NotNull(row);
        Assert.Equal("active", (string)row!["state"]!);
        Assert.Equal(EventFactory.DefaultSid, (string)row["windows_sid"]!);
        Assert.Equal(EventFactory.DefaultUser, (string)row["windows_username"]!);
        Assert.Equal("excel.exe", (string)row["foreground_process"]!);
        Assert.Equal("Orcamento_2026.xlsx - Excel", (string)row["foreground_title"]!);

        // usuário Windows visto registrado em device_users
        var users = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM device_users WHERE device_id = @d AND windows_sid = @s",
            ("d", device.DeviceId), ("s", EventFactory.DefaultSid));
        Assert.Equal(1, users);
    }

    [Fact]
    public async Task SequenciaLockUnlockIdle_SegueOsEventos()
    {
        var (client, device, _) = await SetupAsync("Org Lock Idle");
        var factory = new EventFactory();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);

        // active → idle (retroativo) → lock
        using (await AgentClient.ReadAckAsync(await AgentClient.SendBatchAsync(client, device.DeviceToken,
        [
            factory.Event("ACTIVE_WINDOW_CHANGED", t0, new Dictionary<string, object?>
            {
                ["process_name"] = "chrome.exe", ["window_title"] = "Inbox", ["title_masked"] = false,
            }),
            factory.Event("IDLE_START", t0.AddMinutes(6), new Dictionary<string, object?>
            {
                ["last_input_at"] = t0.AddMinutes(1).UtcDateTime.ToString("o"),
            }),
        ])))
        {
            var row = await TestDb.RowAsync(Cs,
                "SELECT state, state_since FROM device_current_state WHERE device_id = @d", ("d", device.DeviceId));
            Assert.Equal("idle", (string)row!["state"]!);

            // N5: o ocioso começa RETROATIVAMENTE em last_input_at, não no timestamp do evento
            var stateSince = (DateTime)row["state_since"]!;
            Assert.Equal(t0.AddMinutes(1).UtcDateTime, stateSince, TimeSpan.FromSeconds(1));
        }

        using (await AgentClient.ReadAckAsync(await AgentClient.SendBatchAsync(client, device.DeviceToken,
            [factory.Event("LOCK", t0.AddMinutes(7))])))
        {
            var state = await TestDb.ScalarAsync<string>(Cs,
                "SELECT state FROM device_current_state WHERE device_id = @d", ("d", device.DeviceId));
            Assert.Equal("locked", state);
        }
    }

    [Fact]
    public async Task SystemSuspend_ProjetaOffClean()
    {
        var (client, device, _) = await SetupAsync("Org Suspend");
        var factory = new EventFactory();

        using var _ = await AgentClient.ReadAckAsync(await AgentClient.SendBatchAsync(client, device.DeviceToken,
        [
            factory.Event("HEARTBEAT", data: new Dictionary<string, object?>
            {
                ["state"] = "active", ["foreground_process"] = "teams.exe",
            }),
            factory.Event("SYSTEM_SUSPEND", windowsSid: null, windowsUser: null, sessionId: null),
        ]));

        var row = await TestDb.RowAsync(Cs,
            "SELECT state, foreground_process FROM device_current_state WHERE device_id = @d", ("d", device.DeviceId));
        Assert.Equal("off_clean", (string)row!["state"]!);
        Assert.Null(row["foreground_process"]);
    }

    [Fact]
    public async Task ReenvioDeLoteAntigo_NaoRegrideAProjecao()
    {
        var (client, device, _) = await SetupAsync("Org Sem Regressao");
        var factory = new EventFactory();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);

        var oldBatch = new List<Dictionary<string, object?>>
        {
            factory.Event("ACTIVE_WINDOW_CHANGED", t0, new Dictionary<string, object?>
            {
                ["process_name"] = "word.exe", ["window_title"] = "Relatorio.docx", ["title_masked"] = false,
            }),
        };
        var newBatch = new List<Dictionary<string, object?>> { factory.Event("LOCK", t0.AddMinutes(2)) };

        using (await AgentClient.ReadAckAsync(await AgentClient.SendBatchAsync(client, device.DeviceToken, oldBatch))) { }
        using (await AgentClient.ReadAckAsync(await AgentClient.SendBatchAsync(client, device.DeviceToken, newBatch))) { }

        // reenvio do lote ANTIGO (retry tardio): duplicata não pode voltar o estado para active
        using var ack = await AgentClient.ReadAckAsync(await AgentClient.SendBatchAsync(client, device.DeviceToken, oldBatch));
        Assert.Equal(1, ack.RootElement.GetProperty("duplicates").GetInt32());

        var state = await TestDb.ScalarAsync<string>(Cs,
            "SELECT state FROM device_current_state WHERE device_id = @d", ("d", device.DeviceId));
        Assert.Equal("locked", state);
    }

    [Fact]
    public async Task Presence_ListaEstado_ComRegraN6()
    {
        var (client, device, tenantId) = await SetupAsync("Org Presence");
        var viewer = await fixture.CreateUserAsync(tenantId, UserRole.Viewer);
        var factory = new EventFactory();

        using var _ = await AgentClient.ReadAckAsync(await AgentClient.SendBatchAsync(client, device.DeviceToken,
        [
            factory.Event("ACTIVE_WINDOW_CHANGED", data: new Dictionary<string, object?>
            {
                ["process_name"] = "excel.exe", ["window_title"] = "Plan.xlsx", ["title_masked"] = false,
            }),
        ]));

        var token = await AuthClient.LoginAsync(client, viewer);

        // contato recente (≤ 180 s) → estado real
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/dashboard/presence", token))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var item = body.RootElement.GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("device_id").GetGuid() == device.DeviceId);
            Assert.Equal("active", item.GetProperty("presence_state").GetString());
            Assert.Equal("excel.exe", item.GetProperty("foreground_process").GetString());
        }

        // sem contato há > 180 s sem desligamento limpo → "Sem comunicação" (no_data)
        await TestDb.ExecuteAsync(Cs,
            "UPDATE device_current_state SET last_contact_at = now() - interval '10 minutes' WHERE device_id = @d",
            ("d", device.DeviceId));

        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/dashboard/presence", token))
        {
            var response = await client.SendAsync(request);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var item = body.RootElement.GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("device_id").GetGuid() == device.DeviceId);
            Assert.Equal("no_data", item.GetProperty("presence_state").GetString());
            Assert.Equal("active", item.GetProperty("state").GetString()); // estado cru preservado
        }

        // desligamento limpo continua "Desligada" mesmo sem contato (nunca alerta falso)
        await TestDb.ExecuteAsync(Cs,
            "UPDATE device_current_state SET state = 'off_clean' WHERE device_id = @d", ("d", device.DeviceId));

        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get, "/api/v1/dashboard/presence", token))
        {
            var response = await client.SendAsync(request);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var item = body.RootElement.GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("device_id").GetGuid() == device.DeviceId);
            Assert.Equal("off_clean", item.GetProperty("presence_state").GetString());
        }
    }

    [Fact]
    public async Task SkewDeRelogio_CalculadoNoServidor_EPersistido()
    {
        var (client, device, _) = await SetupAsync("Org Skew");

        // agente com relógio ~2 min atrasado: sent_at no passado → offset positivo
        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken, [],
            sentAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        using var _ = await AgentClient.ReadAckAsync(response);

        var offset = await TestDb.ScalarAsync<long>(Cs,
            "SELECT clock_offset_ms FROM devices WHERE id = @d", ("d", device.DeviceId));
        Assert.InRange(offset, 100_000, 140_000); // ~120 s em ms
    }
}

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// TESTES DE CONTRATO da ingestão (gate 11.1.2): envelope 5.2, os 18 tipos 5.3 e o ack 5.5.
/// Tipo desconhecido não rejeita o lote; lote vazio atualiza last_seen_at; duplicata reenviada
/// conta em duplicates sem linha nova; rejeições individuais com reason canônico; lote > 500
/// eventos → 422 batch_too_large.
/// </summary>
[Collection(ApiCollection.Name)]
public class IngestContractTests(ApiTestFixture fixture)
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

    /// <summary>Os 18 tipos canônicos com payloads representativos da tabela 5.3.</summary>
    private static List<Dictionary<string, object?>> AllEighteenTypes(EventFactory factory, DateTimeOffset baseAt)
    {
        var at = baseAt;
        DateTimeOffset Next()
        {
            at = at.AddSeconds(5);
            return at;
        }

        return
        [
            factory.Event("AGENT_START", Next(), new Dictionary<string, object?>
            {
                ["agent_version"] = "1.0.3", ["os_version"] = "Windows 11 Pro", ["os_build"] = "22631",
                ["hostname"] = "NB-TESTE", ["boot_id"] = factory.BootId, ["uptime_ms"] = 120000,
                ["start_reason"] = "boot", ["monitors"] = 2, ["is_vm"] = false, ["join_type"] = "ad",
            }, windowsSid: null, windowsUser: null, sessionId: null),
            factory.Event("SESSION_START", Next(), new Dictionary<string, object?> { ["logon_type"] = "console" }),
            factory.Event("UNLOCK", Next()),
            factory.Event("ACTIVE_WINDOW_CHANGED", Next(), new Dictionary<string, object?>
            {
                ["process_name"] = "excel.exe",
                ["exe_path"] = "C:\\Program Files\\Microsoft Office\\root\\Office16\\EXCEL.EXE",
                ["app_id"] = null, ["window_title"] = "Orcamento_2026.xlsx - Excel", ["title_masked"] = false,
            }),
            factory.Event("IDLE_START", Next(), new Dictionary<string, object?>
            {
                ["last_input_at"] = baseAt.AddSeconds(10).UtcDateTime.ToString("o"),
            }),
            factory.Event("IDLE_END", Next(), new Dictionary<string, object?> { ["idle_duration_ms"] = 300000 }),
            factory.Event("HEARTBEAT", Next(), new Dictionary<string, object?>
            {
                ["state"] = "active", ["foreground_process"] = "excel.exe", ["idle_ms"] = 0, ["queue_depth"] = 3,
                // saúde operacional injetada pelo serviço (F5)
                ["dead_letter_count"] = 0, ["last_reject_code"] = null,
                ["working_set_mb"] = 48, ["queue_db_bytes"] = 2_097_152,
            }),
            factory.Event("LOCK", Next()),
            factory.Event("UNLOCK", Next()),
            factory.Event("TIME_CHANGED", Next(), new Dictionary<string, object?>
            {
                ["old_utc"] = baseAt.UtcDateTime.ToString("o"), ["new_utc"] = baseAt.AddSeconds(40).UtcDateTime.ToString("o"),
                ["delta_ms"] = 40000, ["new_tz_offset_min"] = -180,
            }, windowsSid: null, windowsUser: null, sessionId: null),
            factory.Event("EVENTS_DROPPED", Next(), new Dictionary<string, object?>
            {
                ["count"] = 12, ["oldest_dropped_at"] = baseAt.UtcDateTime.ToString("o"), ["reason"] = "rate_limit",
            }, windowsSid: null, windowsUser: null, sessionId: null),
            factory.Event("AGENT_TAMPER", Next(), new Dictionary<string, object?> { ["reason"] = "helper_killed" },
                windowsSid: null, windowsUser: null, sessionId: null),
            factory.Event("NOTICE_ACK", Next(), new Dictionary<string, object?>
            {
                ["notice_version"] = 1, ["shown_at"] = baseAt.UtcDateTime.ToString("o"),
            }),
            factory.Event("POLICY_APPLIED", Next(), new Dictionary<string, object?> { ["config_version"] = 1 },
                windowsSid: null, windowsUser: null, sessionId: null),
            factory.Event("AGENT_ERROR", Next(), new Dictionary<string, object?>
            {
                ["error_type"] = "System.IO.IOException", ["stack_hash"] = "0123456789abcdef", ["count"] = 2,
            }, windowsSid: null, windowsUser: null, sessionId: null),
            factory.Event("SYSTEM_SUSPEND", Next(), windowsSid: null, windowsUser: null, sessionId: null),
            factory.Event("SYSTEM_RESUME", Next(), new Dictionary<string, object?> { ["sleep_duration_ms"] = 600000 },
                windowsSid: null, windowsUser: null, sessionId: null),
            factory.Event("SESSION_END", Next()),
            factory.Event("AGENT_STOP", Next(), new Dictionary<string, object?> { ["reason"] = "shutdown" },
                windowsSid: null, windowsUser: null, sessionId: null),
        ];
    }

    [Fact]
    public async Task Batch_ComTodosOs18Tipos_TodosAceitosEPersistidos()
    {
        var (client, device, tenantId) = await SetupAsync("Org 18 Tipos");
        var factory = new EventFactory();
        var events = AllEighteenTypes(factory, DateTimeOffset.UtcNow.AddMinutes(-10));
        Assert.Equal(19, events.Count); // 18 tipos (UNLOCK aparece 2×) — sanidade do cenário

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken, events);
        using var ack = await AgentClient.ReadAckAsync(response);

        Assert.Equal(events.Count, ack.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(0, ack.RootElement.GetProperty("duplicates").GetInt32());
        Assert.Equal(0, ack.RootElement.GetProperty("rejected").GetArrayLength());

        var persisted = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM raw_events WHERE device_id = @d AND tenant_id = @t",
            ("d", device.DeviceId), ("t", tenantId));
        Assert.Equal(events.Count, persisted);

        var distinctTypes = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(DISTINCT event_type) FROM raw_events WHERE device_id = @d", ("d", device.DeviceId));
        Assert.Equal(18, distinctTypes);

        // envelope persistido: seq, tz_offset_min, boot_id (exigência da F1 — Seção 10)
        var row = await TestDb.RowAsync(Cs,
            "SELECT seq, tz_offset_min, boot_id, mono_ms, windows_sid, process_name, window_title FROM raw_events WHERE device_id = @d AND event_type = 'ACTIVE_WINDOW_CHANGED'",
            ("d", device.DeviceId));
        Assert.NotNull(row);
        Assert.Equal(-180, (int)row!["tz_offset_min"]!);
        Assert.Equal(Guid.Parse(factory.BootId), (Guid)row["boot_id"]!);
        Assert.Equal(EventFactory.DefaultSid, (string)row["windows_sid"]!);
        Assert.Equal("excel.exe", (string)row["process_name"]!);
        Assert.Equal("Orcamento_2026.xlsx - Excel", (string)row["window_title"]!);

        // seq_max do device acompanha o maior seq visto
        var seqMax = await TestDb.ScalarAsync<long>(Cs,
            "SELECT seq_max FROM devices WHERE id = @d", ("d", device.DeviceId));
        Assert.Equal(factory.LastSeq, seqMax);
    }

    [Fact]
    public async Task TipoDesconhecido_IgnoradoSemRejeitarOLote()
    {
        var (client, device, _) = await SetupAsync("Org Tipo Desconhecido");
        var factory = new EventFactory();
        var events = new List<Dictionary<string, object?>>
        {
            factory.Event("HEARTBEAT", data: new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("APPS_SNAPSHOT"), // CORTADO do MVP: backend deve ignorar
            factory.Event("TIPO_FUTURO_V99"),
            factory.Event("LOCK"),
        };

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken, events);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // JAMAIS 422 por tipo desconhecido

        using var ack = await AgentClient.ReadAckAsync(response);
        Assert.Equal(2, ack.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(0, ack.RootElement.GetProperty("duplicates").GetInt32());
        Assert.Equal(0, ack.RootElement.GetProperty("rejected").GetArrayLength());

        var persisted = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM raw_events WHERE device_id = @d", ("d", device.DeviceId));
        Assert.Equal(2, persisted);
    }

    [Fact]
    public async Task LoteVazio_EhKeepAlive_AtualizaLastSeenAt()
    {
        var (client, device, _) = await SetupAsync("Org Keep Alive");

        var before = await TestDb.ScalarAsync<DateTime?>(Cs,
            "SELECT last_seen_at FROM devices WHERE id = @d", ("d", device.DeviceId));
        Assert.Null(before); // enroll não conta como contato

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken, []);
        using var ack = await AgentClient.ReadAckAsync(response);

        Assert.Equal(0, ack.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(0, ack.RootElement.GetProperty("duplicates").GetInt32());

        var after = await TestDb.ScalarAsync<DateTime?>(Cs,
            "SELECT last_seen_at FROM devices WHERE id = @d", ("d", device.DeviceId));
        Assert.NotNull(after);
    }

    [Fact]
    public async Task DuplicataReenviada_ContaEmDuplicates_SemLinhaNova()
    {
        var (client, device, _) = await SetupAsync("Org Duplicata");
        var factory = new EventFactory();
        var events = new List<Dictionary<string, object?>>
        {
            factory.Event("SESSION_START", data: new Dictionary<string, object?> { ["logon_type"] = "console" }),
            factory.Event("ACTIVE_WINDOW_CHANGED", data: new Dictionary<string, object?>
            {
                ["process_name"] = "chrome.exe", ["window_title"] = "Inbox", ["title_masked"] = false,
            }),
            factory.Event("HEARTBEAT", data: new Dictionary<string, object?> { ["state"] = "active" }),
        };

        using var ack1 = await AgentClient.ReadAckAsync(await AgentClient.SendBatchAsync(client, device.DeviceToken, events));
        Assert.Equal(3, ack1.RootElement.GetProperty("accepted").GetInt32());

        // reenvio integral após timeout simulado: dedupe por event_id (Princípio 6)
        using var ack2 = await AgentClient.ReadAckAsync(await AgentClient.SendBatchAsync(client, device.DeviceToken, events));
        Assert.Equal(0, ack2.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(3, ack2.RootElement.GetProperty("duplicates").GetInt32());
        Assert.Equal(0, ack2.RootElement.GetProperty("rejected").GetArrayLength());

        var persisted = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM raw_events WHERE device_id = @d", ("d", device.DeviceId));
        Assert.Equal(3, persisted);
    }

    [Fact]
    public async Task TimestampsForaDaJanela_RejeitadosIndividualmente_RestoSegue()
    {
        var (client, device, _) = await SetupAsync("Org Janela N9");
        var factory = new EventFactory();
        var now = DateTimeOffset.UtcNow;

        var tooOld = factory.Event("HEARTBEAT", now.AddDays(-15), new Dictionary<string, object?> { ["state"] = "active" });
        var ok = factory.Event("HEARTBEAT", now.AddSeconds(-30), new Dictionary<string, object?> { ["state"] = "active" });
        var inFuture = factory.Event("HEARTBEAT", now.AddMinutes(10), new Dictionary<string, object?> { ["state"] = "idle" });

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken, [tooOld, ok, inFuture]);
        using var ack = await AgentClient.ReadAckAsync(response);

        Assert.Equal(1, ack.RootElement.GetProperty("accepted").GetInt32());

        var rejected = ack.RootElement.GetProperty("rejected").EnumerateArray()
            .ToDictionary(r => r.GetProperty("event_id").GetString()!, r => r.GetProperty("reason").GetString());
        Assert.Equal(2, rejected.Count);
        Assert.Equal("timestamp_too_old", rejected[(string)tooOld["event_id"]!]);
        Assert.Equal("timestamp_in_future", rejected[(string)inFuture["event_id"]!]);

        var persisted = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM raw_events WHERE device_id = @d", ("d", device.DeviceId));
        Assert.Equal(1, persisted);
    }

    [Fact]
    public async Task LoteComMaisDe500Eventos_Retorna422BatchTooLarge()
    {
        var (client, device, _) = await SetupAsync("Org Batch Too Large");
        var factory = new EventFactory();
        var events = Enumerable.Range(0, 501)
            .Select(_ => factory.Event("HEARTBEAT", data: new Dictionary<string, object?> { ["state"] = "active" }))
            .ToList();

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken, events);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("batch_too_large", await response.Content.ReadAsStringAsync());

        var persisted = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM raw_events WHERE device_id = @d", ("d", device.DeviceId));
        Assert.Equal(0, persisted); // lote inteiro rejeitado
    }

    [Fact]
    public async Task JsonMalformado_Retorna422()
    {
        var (client, device, _) = await SetupAsync("Org Json Malformado");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/batch")
        {
            Content = new StringContent("{ \"events\": [ ", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", device.DeviceToken);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Batch_ComGzip_EhAceito()
    {
        var (client, device, _) = await SetupAsync("Org Gzip");
        var factory = new EventFactory();
        var body = new Dictionary<string, object?>
        {
            ["batch_id"] = Guid.NewGuid().ToString(),
            ["agent_version"] = "1.0.0",
            ["sent_at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("o"),
            ["config_version"] = 1,
            ["events"] = new[] { factory.Event("HEARTBEAT", data: new Dictionary<string, object?> { ["state"] = "active" }) },
        };

        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            await JsonSerializer.SerializeAsync(gzip, body);
        }

        var content = new ByteArrayContent(compressed.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/batch") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", device.DeviceToken);

        var response = await client.SendAsync(request);
        using var ack = await AgentClient.ReadAckAsync(response);
        Assert.Equal(1, ack.RootElement.GetProperty("accepted").GetInt32());
    }

    [Fact]
    public async Task Ack_TemAFormaCanonicaDaSecao55()
    {
        var (client, device, _) = await SetupAsync("Org Forma do Ack");
        var factory = new EventFactory();

        var response = await AgentClient.SendBatchAsync(
            client, device.DeviceToken,
            [factory.Event("HEARTBEAT", data: new Dictionary<string, object?> { ["state"] = "active" })],
            configVersion: 1);
        using var ack = await AgentClient.ReadAckAsync(response);
        var root = ack.RootElement;

        Assert.True(root.TryGetProperty("accepted", out _));
        Assert.True(root.TryGetProperty("duplicates", out _));
        Assert.True(root.TryGetProperty("rejected", out var rejected) && rejected.ValueKind == JsonValueKind.Array);
        Assert.True(root.TryGetProperty("server_time", out _));
        Assert.True(root.TryGetProperty("config_version", out _));
        Assert.True(root.TryGetProperty("config", out var config));
        Assert.Equal(JsonValueKind.Null, config.ValueKind); // config_version igual → config: null
        Assert.True(root.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array);
    }
}

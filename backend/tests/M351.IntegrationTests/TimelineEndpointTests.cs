using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Intervalization;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// GET /api/v1/timeline/device (Seção 7.4/8.5): contrato, resolução N21, rodapé,
/// data_incomplete na resposta (cenário lacuna-de-seq da 11.2), fuso do tenant,
/// ETag para dias passados, cauda viva de hoje e auditoria (DoD 11.3).
/// </summary>
[Collection(ApiCollection.Name)]
public class TimelineEndpointTests(ApiTestFixture fixture)
{
    // "ontem" no dia LOCAL do tenant (GMT-3), não no dia UTC: entre 21:00 e 00:00 locais a
    // data UTC já virou, e UtcNow.Date.AddDays(-1) cairia no dia local CORRENTE — a timeline
    // trataria o dia como "hoje" (sem ETag) e o teste do 304 flakearia toda noite.
    private static readonly DateTimeOffset Base =
        new(DateTime.UtcNow.AddHours(-3).Date.AddDays(-1), TimeSpan.Zero);

    private static DateTimeOffset T(int h, int m, int s = 0) => Base.AddHours(h).AddMinutes(m).AddSeconds(s);
    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("o");

    /// <summary>Dia local (America/Sao_Paulo, GMT-3) de um instante UTC.</summary>
    private static string LocalDate(DateTimeOffset utc) => utc.AddHours(-3).ToString("yyyy-MM-dd");

    private async Task<(HttpClient Client, EnrolledDevice Device, string Token)> SetupAsync(string hostname)
    {
        var org = await fixture.CreateOrganizationAsync($"Timeline {Guid.NewGuid():N}"[..20]);
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var device = await AgentClient.EnrollAsync(client, fullKey, hostname: hostname);
        var token = await AuthClient.LoginAsync(client, viewer);
        return (client, device, token);
    }

    private async Task RunPipelineAsync()
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE devices SET clock_offset_ms = 0 WHERE clock_offset_ms BETWEEN -5000 AND 5000");
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        await new IntervalizationService(dataSource).RunOnceAsync();
    }

    private static async Task<JsonDocument> GetTimelineAsync(
        HttpClient client, string token, Guid deviceId, string date, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/timeline/device?device_id={deviceId}&date={date}", token);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"esperado {expected}, veio {response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private async Task SeedDiaPadraoAsync(HttpClient client, EnrolledDevice device)
    {
        var factory = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            factory.Event("ACTIVE_WINDOW_CHANGED", T(14, 0), new Dictionary<string, object?>
            {
                ["process_name"] = "excel.exe", ["window_title"] = "Orcamento_2026.xlsx - Excel",
            }),
            factory.Event("HEARTBEAT", T(14, 8), new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("HEARTBEAT", T(14, 16), new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("HEARTBEAT", T(14, 24), new Dictionary<string, object?> { ["state"] = "active" }),
            factory.Event("IDLE_START", T(14, 31, 40), new Dictionary<string, object?>
            {
                ["last_input_at"] = Iso(T(14, 26, 40)),
            }),
            factory.Event("IDLE_END", T(14, 40)),
            factory.Event("LOCK", T(14, 45)),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();
    }

    // ------------------------------------------------------------ contrato + rodapé
    [Fact]
    public async Task DiaPassado_ContratoCompleto_RodapeConsistente()
    {
        var (client, device, token) = await SetupAsync("NB-TL-SHAPE");
        await SeedDiaPadraoAsync(client, device);

        using var doc = await GetTimelineAsync(client, token, device.DeviceId, LocalDate(T(14, 0)));
        var root = doc.RootElement;

        Assert.Equal(device.DeviceId, root.GetProperty("device_id").GetGuid());
        Assert.Equal("America/Sao_Paulo", root.GetProperty("timezone").GetString());
        Assert.Equal(60, root.GetProperty("resolution_sec").GetInt32());
        Assert.False(root.GetProperty("data_incomplete").GetBoolean());

        var intervals = root.GetProperty("intervals").EnumerateArray().ToList();
        Assert.True(intervals.Count >= 3, $"esperava >= 3 intervalos, veio {intervals.Count}");

        var active = intervals.First(i => i.GetProperty("state").GetString() == "active");
        Assert.Equal(T(14, 0), active.GetProperty("started_at").GetDateTimeOffset());
        Assert.Equal(T(14, 26, 40), active.GetProperty("ended_at").GetDateTimeOffset()); // N5
        Assert.Equal("excel.exe", active.GetProperty("app").GetProperty("process_name").GetString());
        Assert.Equal("Orcamento_2026.xlsx - Excel", active.GetProperty("window_title").GetString());

        var idle = intervals.First(i => i.GetProperty("state").GetString() == "idle");
        Assert.Equal(JsonValueKind.Null, idle.GetProperty("app").ValueKind); // app só em active

        // rodapé: active 1600s (14:00→14:26:40) + 300s (14:40→14:45); idle 800s; locked 0
        var summary = root.GetProperty("summary");
        Assert.Equal(1900, summary.GetProperty("seconds_active").GetInt64());
        Assert.Equal(800, summary.GetProperty("seconds_idle").GetInt64());
        Assert.Equal(0, summary.GetProperty("seconds_locked").GetInt64());
        Assert.Equal(2700, summary.GetProperty("seconds_on").GetInt64());
        Assert.Equal(T(14, 0), summary.GetProperty("first_event_at").GetDateTimeOffset());
        Assert.Equal(T(14, 45), summary.GetProperty("last_event_at").GetDateTimeOffset());
    }

    // ------------------------------------------------------------ isolamento e validação
    [Fact]
    public async Task DeviceDeOutroTenant_Retorna404()
    {
        var (clientA, deviceA, _) = await SetupAsync("NB-TL-ORG-A");
        _ = clientA;
        var (clientB, _, tokenB) = await SetupAsync("NB-TL-ORG-B");

        (await GetTimelineAsync(clientB, tokenB, deviceA.DeviceId, LocalDate(T(14, 0)), HttpStatusCode.NotFound)).Dispose();
    }

    [Fact]
    public async Task SemToken_Retorna401()
    {
        var client = fixture.CreateApiClient();
        var response = await client.GetAsync($"/api/v1/timeline/device?device_id={Guid.NewGuid()}&date=2026-06-09");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DataInvalida_Retorna400()
    {
        var (client, device, token) = await SetupAsync("NB-TL-DATA");
        (await GetTimelineAsync(client, token, device.DeviceId, "09-06-2026", HttpStatusCode.BadRequest)).Dispose();
    }

    // ------------------------------------------------------------ lacuna-de-seq na RESPOSTA (11.2)
    [Fact]
    public async Task LacunaDeSeq_FlagPresenteNaRespostaDaTimeline()
    {
        var (client, device, token) = await SetupAsync("NB-TL-LACUNA");
        var f1 = new EventFactory(startSeq: 100);
        var ack1 = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f1.Event("ACTIVE_WINDOW_CHANGED", T(9, 0), new Dictionary<string, object?> { ["process_name"] = "a.exe" }),
            f1.Event("HEARTBEAT", T(9, 1), new Dictionary<string, object?> { ["state"] = "active" }),
        });
        (await AgentClient.ReadAckAsync(ack1)).Dispose();
        var f2 = new EventFactory(startSeq: 105);
        var ack2 = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            f2.Event("ACTIVE_WINDOW_CHANGED", T(9, 5), new Dictionary<string, object?> { ["process_name"] = "b.exe" }),
            f2.Event("LOCK", T(9, 9)),
        });
        (await AgentClient.ReadAckAsync(ack2)).Dispose();
        await RunPipelineAsync();

        using var doc = await GetTimelineAsync(client, token, device.DeviceId, LocalDate(T(9, 0)));
        Assert.True(doc.RootElement.GetProperty("data_incomplete").GetBoolean()); // OR global
        Assert.Contains(doc.RootElement.GetProperty("intervals").EnumerateArray(),
            i => i.GetProperty("data_incomplete").GetBoolean());
    }

    // ------------------------------------------------------------ ETag (8.5)
    [Fact]
    public async Task DiaPassado_ETag_E_304()
    {
        var (client, device, token) = await SetupAsync("NB-TL-ETAG");
        await SeedDiaPadraoAsync(client, device);
        var date = LocalDate(T(14, 0));

        using var first = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/timeline/device?device_id={device.DeviceId}&date={date}", token);
        var r1 = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var etag = r1.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrEmpty(etag), "ETag ausente para dia passado");

        using var second = AuthClient.AuthorizedRequest(
            HttpMethod.Get, $"/api/v1/timeline/device?device_id={device.DeviceId}&date={date}", token);
        second.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var r2 = await client.SendAsync(second);
        Assert.Equal(HttpStatusCode.NotModified, r2.StatusCode);
    }

    // ------------------------------------------------------------ auditoria (DoD 11.3)
    [Fact]
    public async Task Visualizacao_GravaAuditLog()
    {
        var (client, device, token) = await SetupAsync("NB-TL-AUDIT");
        await SeedDiaPadraoAsync(client, device);

        (await GetTimelineAsync(client, token, device.DeviceId, LocalDate(T(14, 0)))).Dispose();

        var count = await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE action = 'view_timeline' AND target_id = @d",
            ("d", device.DeviceId));
        Assert.True(count >= 1, "view_timeline não foi auditado");
    }

    // ------------------------------------------------------------ cauda viva de hoje
    [Fact]
    public async Task Hoje_SilencioSemDesligamento_GanhaCaudaNoData()
    {
        var (client, device, token) = await SetupAsync("NB-TL-CAUDA");
        var now = DateTimeOffset.UtcNow;
        var factory = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            factory.Event("ACTIVE_WINDOW_CHANGED", now.AddMinutes(-40), new Dictionary<string, object?>
            {
                ["process_name"] = "chrome.exe",
            }),
            factory.Event("HEARTBEAT", now.AddMinutes(-31), new Dictionary<string, object?> { ["state"] = "active" }),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();

        // silêncio "real": último contato há 31 min (a ingestão tinha marcado agora)
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
            "UPDATE device_current_state SET last_contact_at = now() - interval '31 minutes' WHERE device_id = @d",
            ("d", device.DeviceId));

        using var doc = await GetTimelineAsync(client, token, device.DeviceId, LocalDate(now));
        var intervals = doc.RootElement.GetProperty("intervals").EnumerateArray().ToList();
        Assert.True(intervals.Count >= 2);
        var tail = intervals[^1];
        Assert.Equal("no_data", tail.GetProperty("state").GetString());
        Assert.True(tail.GetProperty("ended_at").GetDateTimeOffset() >= now.AddSeconds(-30));
    }

    [Fact]
    public async Task Hoje_DesligamentoLimpo_GanhaCaudaOffClean()
    {
        var (client, device, token) = await SetupAsync("NB-TL-CAUDA-OFF");
        var now = DateTimeOffset.UtcNow;
        var factory = new EventFactory();
        var ack = await AgentClient.SendBatchAsync(client, device.DeviceToken, new[]
        {
            factory.Event("ACTIVE_WINDOW_CHANGED", now.AddMinutes(-20), new Dictionary<string, object?>
            {
                ["process_name"] = "chrome.exe",
            }),
            factory.Event("SYSTEM_SUSPEND", now.AddMinutes(-15),
                windowsSid: null, windowsUser: null, sessionId: null),
        });
        (await AgentClient.ReadAckAsync(ack)).Dispose();
        await RunPipelineAsync();

        using var doc = await GetTimelineAsync(client, token, device.DeviceId, LocalDate(now));
        var intervals = doc.RootElement.GetProperty("intervals").EnumerateArray().ToList();
        var tail = intervals[^1];
        Assert.Equal("off_clean", tail.GetProperty("state").GetString()); // "Desligada/suspensa" ao vivo
        Assert.True(tail.GetProperty("ended_at").GetDateTimeOffset() >= now.AddSeconds(-30));
    }
}

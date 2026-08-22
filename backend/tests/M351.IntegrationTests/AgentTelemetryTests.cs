using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using M351.IntegrationTests.Support;

namespace M351.IntegrationTests;

/// <summary>
/// Telemetria de erro do agente vista do lado do INGEST (F5):
///  - AGENT_ERROR é o 18º tipo canônico (Seção 5.3) e persiste o payload sem mensagem crua;
///  - EVENTS_DROPPED aceita o reason novo pipe_overflow (lista da Seção 5.3);
///  - HEARTBEAT com os campos operacionais novos NÃO é rejeitado (payload extra é tolerado);
///  - POST /api/v1/agent/diagnostics recebe o ZIP do --diag por device token, com cap de 10 MB.
/// </summary>
[Collection(ApiCollection.Name)]
public class AgentTelemetryTests(ApiTestFixture fixture)
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

    // ------------------------------------------------------------------ AGENT_ERROR

    [Fact]
    public async Task AgentError_EhAceitoEPersistidoSemMensagemCrua()
    {
        var (client, device, _) = await SetupAsync("Org Agent Error");
        var factory = new EventFactory();

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken,
        [
            factory.Event("AGENT_ERROR", data: new Dictionary<string, object?>
            {
                ["error_type"] = "System.Net.Http.HttpRequestException",
                ["stack_hash"] = "a1b2c3d4e5f60718",
                ["count"] = 5,
            }, windowsSid: null, windowsUser: null, sessionId: null),
        ]);

        using var ack = await AgentClient.ReadAckAsync(response);
        Assert.Equal(1, ack.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(0, ack.RootElement.GetProperty("rejected").GetArrayLength());

        var row = await TestDb.RowAsync(Cs,
            "SELECT event_type, payload::text AS payload FROM raw_events WHERE device_id = @d",
            ("d", device.DeviceId));
        Assert.NotNull(row);
        Assert.Equal("AGENT_ERROR", (string)row!["event_type"]!);

        using var payload = JsonDocument.Parse((string)row["payload"]!);
        Assert.Equal("System.Net.Http.HttpRequestException", payload.RootElement.GetProperty("error_type").GetString());
        Assert.Equal("a1b2c3d4e5f60718", payload.RootElement.GetProperty("stack_hash").GetString());
        Assert.Equal(5, payload.RootElement.GetProperty("count").GetInt64());
        Assert.False(payload.RootElement.TryGetProperty("message", out _)); // contrato de privacidade
    }

    // ------------------------------------------------------------------ EVENTS_DROPPED

    [Fact]
    public async Task EventsDropped_ComReasonPipeOverflow_EhAceito()
    {
        var (client, device, _) = await SetupAsync("Org Pipe Overflow");
        var factory = new EventFactory();

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken,
        [
            factory.Event("EVENTS_DROPPED", data: new Dictionary<string, object?>
            {
                ["count"] = 37,
                ["oldest_dropped_at"] = DateTimeOffset.UtcNow.AddMinutes(-3).UtcDateTime.ToString("o"),
                ["reason"] = "pipe_overflow",
            }, windowsSid: null, windowsUser: null, sessionId: null),
        ]);

        using var ack = await AgentClient.ReadAckAsync(response);
        Assert.Equal(1, ack.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(0, ack.RootElement.GetProperty("rejected").GetArrayLength());

        var reason = await TestDb.ScalarAsync<string>(Cs,
            "SELECT payload->>'reason' FROM raw_events WHERE device_id = @d AND event_type = 'EVENTS_DROPPED'",
            ("d", device.DeviceId));
        Assert.Equal("pipe_overflow", reason);
    }

    // ------------------------------------------------------------------ HEARTBEAT operacional

    [Fact]
    public async Task Heartbeat_ComCamposOperacionaisNovos_NaoEhRejeitado()
    {
        var (client, device, _) = await SetupAsync("Org Heartbeat Saude");
        var factory = new EventFactory();

        var response = await AgentClient.SendBatchAsync(client, device.DeviceToken,
        [
            factory.Event("HEARTBEAT", data: new Dictionary<string, object?>
            {
                ["state"] = "active", ["foreground_process"] = "chrome.exe", ["idle_ms"] = 0,
                ["queue_depth"] = 12, ["dead_letter_count"] = 3, ["last_reject_code"] = "invalid_event",
                ["working_set_mb"] = 64, ["queue_db_bytes"] = 5_242_880,
            }),
        ]);

        using var ack = await AgentClient.ReadAckAsync(response);
        Assert.Equal(1, ack.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(0, ack.RootElement.GetProperty("rejected").GetArrayLength());

        // o estado canônico continua projetado e o payload extra é preservado como veio
        var row = await TestDb.RowAsync(Cs,
            "SELECT payload::text AS payload FROM raw_events WHERE device_id = @d AND event_type = 'HEARTBEAT'",
            ("d", device.DeviceId));
        using var payload = JsonDocument.Parse((string)row!["payload"]!);
        Assert.Equal(3, payload.RootElement.GetProperty("dead_letter_count").GetInt64());
        Assert.Equal("invalid_event", payload.RootElement.GetProperty("last_reject_code").GetString());
        Assert.Equal(64, payload.RootElement.GetProperty("working_set_mb").GetInt64());
        Assert.Equal(5_242_880, payload.RootElement.GetProperty("queue_db_bytes").GetInt64());

        var state = await TestDb.ScalarAsync<string>(Cs,
            "SELECT state FROM device_current_state WHERE device_id = @d", ("d", device.DeviceId));
        Assert.Equal("active", state);
    }

    // ------------------------------------------------------------------ upload de diagnóstico

    /// <summary>ZIP mínimo válido (arquivo vazio, assinatura PK) para exercitar o endpoint.</summary>
    private static byte[] MinimalZip()
    {
        using var buffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("logs/service-20260821.log");
            using var writer = new StreamWriter(entry.Open());
            writer.WriteLine("2026-08-21 10:00:00 [INF] Serviço iniciando…");
        }
        return buffer.ToArray();
    }

    private static HttpRequestMessage DiagnosticsRequest(string token, byte[] zip, string contentType = "application/zip")
    {
        var content = new ByteArrayContent(zip);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/diagnostics") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Diagnostics_ComZipEDeviceToken_SalvaNoVolumeDeExports()
    {
        var (client, device, _) = await SetupAsync("Org Diag Ok");
        var zip = MinimalZip();

        using var request = DiagnosticsRequest(device.DeviceToken, zip);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(zip.Length, body.RootElement.GetProperty("received_bytes").GetInt32());

        var directory = Path.Combine(fixture.ExportsDirectory, "diagnostics");
        var saved = Directory.GetFiles(directory, $"{device.DeviceId}-*.zip");
        var file = Assert.Single(saved);
        Assert.Equal(zip.Length, new FileInfo(file).Length);
        Assert.Equal(zip, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task Diagnostics_ComMultipart_TambemEhAceito()
    {
        var (client, device, _) = await SetupAsync("Org Diag Multipart");
        var zip = MinimalZip();

        var fileContent = new ByteArrayContent(zip);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var multipart = new MultipartFormDataContent { { fileContent, "file", "diag.zip" } };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/diagnostics") { Content = multipart };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", device.DeviceToken);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var saved = Directory.GetFiles(Path.Combine(fixture.ExportsDirectory, "diagnostics"),
            $"{device.DeviceId}-*.zip");
        Assert.Single(saved);
    }

    [Fact]
    public async Task Diagnostics_SemDeviceToken_Retorna401()
    {
        var (client, _, _) = await SetupAsync("Org Diag Sem Token");

        var content = new ByteArrayContent(MinimalZip());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/diagnostics") { Content = content };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Diagnostics_ContentTypeErrado_Retorna415()
    {
        var (client, device, _) = await SetupAsync("Org Diag Tipo Errado");

        using var request = DiagnosticsRequest(device.DeviceToken, MinimalZip(), "application/json");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Contains("unsupported_media_type", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Diagnostics_CorpoQueNaoEhZip_Retorna422()
    {
        var (client, device, _) = await SetupAsync("Org Diag Nao Zip");

        using var request = DiagnosticsRequest(device.DeviceToken, Encoding.UTF8.GetBytes("isto nao e um zip"));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("invalid_zip", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Diagnostics_AcimaDe10MB_Retorna413()
    {
        var (client, device, _) = await SetupAsync("Org Diag Grande");
        var big = new byte[11 * 1024 * 1024];
        big[0] = (byte)'P';
        big[1] = (byte)'K';

        using var request = DiagnosticsRequest(device.DeviceToken, big);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}

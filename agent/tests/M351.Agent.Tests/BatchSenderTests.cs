using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Net;
using M351.Agent.Core.Security;
using M351.Agent.Core.Storage;
using M351.Agent.Tests.Support;
using Xunit;

namespace M351.Agent.Tests;

public class BatchSenderTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                var raw = await request.Content.ReadAsByteArrayAsync(ct);
                if (request.Content.Headers.ContentEncoding.Contains("gzip"))
                {
                    using var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
                    using var reader = new StreamReader(gz, Encoding.UTF8);
                    Bodies.Add(await reader.ReadToEndAsync(ct));
                }
                else
                {
                    Bodies.Add(Encoding.UTF8.GetString(raw));
                }
            }
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static (TempQueue Temp, AgentStateStore State, BatchSender Sender, FakeHandler Handler)
        Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var temp = new TempQueue();
        var state = new AgentStateStore(temp.Queue, new PlaintextSecretProtector())
        {
            DeviceId = "01976f00-aaaa-7bbb-8ccc-dddddddddddd",
            DeviceToken = "dt_teste",
            ServerUrl = "http://localhost:5080"
        };
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler);
        var processor = new AckProcessor(temp.Queue, state, TestEvents.Factory(), new NullLogSink());
        var sender = new BatchSender(http, temp.Queue, state, processor, null, new NullLogSink());
        return (temp, state, sender, handler);
    }

    private const string OkAck =
        """{"accepted":3,"duplicates":0,"rejected":[],"server_time":"2026-06-09T14:32:07.852Z","config_version":0,"config":null,"commands":null}""";

    [Fact]
    public async Task Envio_ok_marca_eventos_como_enviados_somente_apos_ack()
    {
        var (temp, _, sender, handler) = Build(_ => Json(HttpStatusCode.OK, OkAck));
        using var _1 = temp;
        var factory = TestEvents.Factory();
        for (var i = 0; i < 3; i++) temp.Queue.Enqueue(TestEvents.Heartbeat(factory));

        var ok = await sender.SendOnceAsync(CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(0, temp.Queue.UnsentCount);   // marcados como enviados…
        Assert.Equal(3, temp.Queue.TotalCount);    // …mas só apagados no purge periódico

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:5080/api/v1/ingest/batch", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("dt_teste", request.Headers.Authorization.Parameter);
        Assert.Contains("gzip", request.Content!.Headers.ContentEncoding);

        var body = JsonSerializer.Deserialize(handler.Bodies[0], AgentJsonContext.Default.BatchRequest)!;
        Assert.Equal(3, body.Events.Count);
        Assert.Equal("1.0.0", body.AgentVersion);
    }

    [Fact]
    public async Task Falha_de_rede_NAO_marca_eventos_reenvio_seguro()
    {
        var (temp, _, sender, _) = Build(_ => throw new HttpRequestException("offline"));
        using var _1 = temp;
        temp.Queue.Enqueue(TestEvents.Heartbeat(TestEvents.Factory()));

        var ok = await sender.SendOnceAsync(CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(1, temp.Queue.UnsentCount); // nada perdido, nada marcado
    }

    [Fact]
    public async Task Http_422_move_lote_para_dead_letter_e_nao_trava_fila()
    {
        var (temp, _, sender, _) = Build(_ => Json(HttpStatusCode.UnprocessableEntity,
            """{"type":"about:blank","title":"batch_too_large"}"""));
        using var _1 = temp;
        temp.Queue.Enqueue(TestEvents.Heartbeat(TestEvents.Factory()));

        var ok = await sender.SendOnceAsync(CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(0, temp.Queue.UnsentCount); // lote ruim não fica preso na fila
    }

    [Fact]
    public async Task Ack_com_UNENROLL_zera_fila_e_token()
    {
        const string unenrollAck =
            """{"accepted":1,"duplicates":0,"rejected":[],"server_time":"2026-06-09T14:32:07.852Z","config_version":0,"config":null,"commands":[{"id":"c1","type":"UNENROLL","payload":{}}]}""";
        var (temp, state, sender, _) = Build(_ => Json(HttpStatusCode.OK, unenrollAck));
        using var _1 = temp;
        temp.Queue.Enqueue(TestEvents.Heartbeat(TestEvents.Factory()));

        await sender.SendOnceAsync(CancellationToken.None);

        Assert.Equal(0, temp.Queue.TotalCount);
        Assert.Null(state.DeviceToken);
        Assert.True(state.Unenrolled);
    }

    [Fact]
    public async Task Lote_vazio_e_valido_como_keep_alive()
    {
        var (temp, _, sender, handler) = Build(_ => Json(HttpStatusCode.OK,
            """{"accepted":0,"duplicates":0,"rejected":[],"server_time":"2026-06-09T14:32:07.852Z","config_version":0,"config":null,"commands":null}"""));
        using var _1 = temp;

        var ok = await sender.SendOnceAsync(CancellationToken.None);

        Assert.True(ok);
        var body = JsonSerializer.Deserialize(handler.Bodies[0], AgentJsonContext.Default.BatchRequest)!;
        Assert.Empty(body.Events);
    }
}

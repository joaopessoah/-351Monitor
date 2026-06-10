using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Queue;
using M351.Agent.Core.Storage;

namespace M351.Agent.Core.Net;

/// <summary>
/// Envio em lote ao backend — N3: a cada 30 s OU 500 eventos (o que vier primeiro);
/// lote vazio = keep-alive. Eventos só são apagados da fila APÓS ack (idempotência por
/// event_id garante reenvio seguro). gzip no request (Seção 5.4). Retry N14; 401 → N15.
/// </summary>
public sealed class BatchSender
{
    public const int MaxBatchSize = 500; // N3 / 5.6

    private readonly HttpClient _http;
    private readonly SqliteEventQueue _queue;
    private readonly AgentStateStore _state;
    private readonly AckProcessor _ackProcessor;
    private readonly EnrollmentClient? _enrollment;
    private readonly ILogSink _log;
    private readonly RetryPolicy _retry = new();

    private DateTimeOffset _lastSendAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextAllowedSend = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPurge = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastReenrollAttempt = DateTimeOffset.MinValue;
    private bool _needsReenroll;
    private int _currentMaxBatch = MaxBatchSize;
    private bool _offlineLogged;

    public TimeSpan SendInterval { get; init; } = TimeSpan.FromSeconds(30); // N3
    public DateTimeOffset? LastSuccessfulSendAt { get; private set; }

    public BatchSender(HttpClient http, SqliteEventQueue queue, AgentStateStore state,
        AckProcessor ackProcessor, EnrollmentClient? enrollment, ILogSink log)
    {
        _http = http;
        _queue = queue;
        _state = state;
        _ackProcessor = ackProcessor;
        _enrollment = enrollment;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error("Falha inesperada no ciclo de envio (continua).", ex);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (now - _lastPurge >= TimeSpan.FromMinutes(10))
        {
            var purged = _queue.PurgeSent(); // deleção física dos ackados a cada 10 min (Seção 6.4)
            _lastPurge = now;
            if (purged > 0) _log.Info($"Fila: {purged} eventos ackados removidos fisicamente.");
        }

        if (!_state.IsEnrolled)
        {
            await MaybeReenrollAsync(ct);
            return;
        }

        if (_needsReenroll)
        {
            await MaybeReenrollAsync(ct);
            if (_needsReenroll) return; // ainda sem token novo: mantém a fila e espera (N15)
        }

        if (now < _nextAllowedSend) return;

        var depth = _queue.UnsentCount;
        var due = now - _lastSendAttempt >= SendInterval;
        if (depth >= MaxBatchSize || due)
        {
            await SendOnceAsync(ct);
        }
    }

    /// <summary>Um envio (inclusive lote vazio como keep-alive). Retorna true em HTTP 200.</summary>
    public async Task<bool> SendOnceAsync(CancellationToken ct)
    {
        _lastSendAttempt = DateTimeOffset.UtcNow;

        var serverUrl = _state.ServerUrl;
        var token = _state.DeviceToken;
        if (serverUrl is null || token is null) return false;

        var events = _queue.PeekBatch(Math.Min(_currentMaxBatch, MaxBatchSize));
        var batch = BuildBatch(events, AgentVersionInfo.Current, _state.ConfigVersion, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(batch, AgentJsonContext.Default.BatchRequest);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/api/v1/ingest/batch");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var content = new ByteArrayContent(Gzip(json));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");
            request.Content = content;
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Backoff();
            if (!_offlineLogged)
            {
                _log.Warn($"Servidor inacessível — eventos permanecem na fila local ({_queue.UnsentCount} pendentes). " +
                          $"Nova tentativa com backoff (N14).");
                _offlineLogged = true;
            }
            return false;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                _offlineLogged = false;
                AckResponse? ack = null;
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    ack = await JsonSerializer.DeserializeAsync(stream, AgentJsonContext.Default.AckResponse, ct);
                }
                catch (JsonException ex)
                {
                    _log.Error("Ack ilegível (eventos NÃO marcados como enviados; reenvio é seguro).", ex);
                    Backoff();
                    return false;
                }

                if (ack is null) { Backoff(); return false; }

                // eventos só saem da fila APÓS o ack (accepted+duplicates+rejected = processados)
                if (events.Count > 0) _queue.MarkSent(events.Select(e => e.Seq));
                _retry.Reset();
                _currentMaxBatch = MaxBatchSize;
                _nextAllowedSend = DateTimeOffset.MinValue;
                LastSuccessfulSendAt = DateTimeOffset.UtcNow;

                if (events.Count > 0 || ack.Config is not null)
                {
                    _log.Info($"Lote enviado: {events.Count} eventos | ack: accepted={ack.Accepted} " +
                              $"duplicates={ack.Duplicates} rejected={ack.Rejected.Count}" +
                              (ack.Config is not null ? $" | config v{ack.ConfigVersion} recebida" : ""));
                }

                _ackProcessor.Process(ack);
                return true;
            }

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized: // 401: transitório — mantém fila, re-enroll a cada 1 h (N15)
                    _log.Warn("401 do servidor: token inválido/revogado. Fila mantida; re-enroll a cada 1 h (N15).");
                    _needsReenroll = true;
                    _nextAllowedSend = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
                    return false;

                case HttpStatusCode.UnprocessableEntity: // 422: lote ruim não pode travar a fila
                    _log.Warn($"422 do servidor: lote movido para dead_letter ({events.Count} eventos).");
                    _queue.MoveToDeadLetter(json);
                    if (events.Count > 0) _queue.MarkSent(events.Select(e => e.Seq));
                    return false;

                case HttpStatusCode.RequestEntityTooLarge: // 413: reduz o lote e tenta de novo
                    _currentMaxBatch = Math.Max(_currentMaxBatch / 2, 25);
                    _log.Warn($"413 do servidor: lote reduzido para {_currentMaxBatch} eventos.");
                    return false;

                case HttpStatusCode.TooManyRequests:
                case HttpStatusCode.ServiceUnavailable:
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? _retry.NextDelay();
                    _nextAllowedSend = DateTimeOffset.UtcNow + retryAfter;
                    _log.Warn($"{(int)response.StatusCode} do servidor: aguardando {retryAfter.TotalSeconds:F0}s (Retry-After respeitado).");
                    return false;

                default:
                    Backoff();
                    _log.Warn($"HTTP {(int)response.StatusCode} do servidor: retry com backoff (tentativa {_retry.Failures}).");
                    return false;
            }
        }
    }

    private void Backoff()
    {
        _nextAllowedSend = DateTimeOffset.UtcNow + _retry.NextDelay();
    }

    private async Task MaybeReenrollAsync(CancellationToken ct)
    {
        if (_enrollment is null || _state.Unenrolled) return;
        var key = _state.EnrollmentKey;
        var server = _state.ServerUrl;
        if (key is null || server is null) return;

        // N15: a cada 1 h (primeira tentativa imediata)
        if (DateTimeOffset.UtcNow - _lastReenrollAttempt < TimeSpan.FromHours(1)) return;
        _lastReenrollAttempt = DateTimeOffset.UtcNow;

        _log.Info("Tentando (re-)enroll com a enrollment key persistida…");
        if (await _enrollment.EnrollAsync(server, key, ct))
        {
            _needsReenroll = false;
            _retry.Reset();
            _nextAllowedSend = DateTimeOffset.MinValue;
            _log.Info("(Re-)enroll bem-sucedido: envio normal retomado.");
        }
    }

    /// <summary>Montagem do lote (Seção 5.4) — pública para os testes de contrato.</summary>
    public static BatchRequest BuildBatch(IReadOnlyList<AgentEvent> events, string agentVersion,
        int configVersion, DateTimeOffset sentAt) => new()
    {
        BatchId = Uuid7.NewUuid7(sentAt).ToString(),
        AgentVersion = agentVersion,
        SentAt = Iso.Format(sentAt),
        ConfigVersion = configVersion,
        Events = events.ToList()
    };

    private static byte[] Gzip(string json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new StreamWriter(gzip))
        {
            writer.Write(json);
        }
        return output.ToArray();
    }
}

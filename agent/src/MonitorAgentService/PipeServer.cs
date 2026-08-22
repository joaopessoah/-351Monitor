using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using M351.Agent.Core;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Events;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Net;

namespace MonitorAgentService;

/// <summary>
/// Lado serviço do IPC \\.\pipe\monitoragent.{sessionId} (Seção 6.1): JSON delimitado por linha.
/// helper → serviço: eventos / updates / drops. serviço → helper: config (+ device_id, boot_id).
/// DACL: ReadWrite só para o SID do usuário da sessão + SYSTEM (helper não acessa fila nem token).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeServer : IDisposable
{
    private readonly int _sessionId;
    private readonly SecurityIdentifier? _userSid;
    private readonly AgentRuntime _runtime;
    private readonly ILogSink _log;
    private readonly Action _onPipeDenied;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _writerGate = new();
    private StreamWriter? _writer;
    private bool _started;

    public PipeServer(int sessionId, SecurityIdentifier? userSid, AgentRuntime runtime, ILogSink log,
        Action onPipeDenied)
    {
        _sessionId = sessionId;
        _userSid = userSid;
        _runtime = runtime;
        _log = log;
        _onPipeDenied = onPipeDenied;
    }

    public void Start()
    {
        lock (_writerGate)
        {
            if (_started) return;
            _started = true;
        }
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // Quota de buffer > 0: com quota zero toda escrita servico→helper vira
                // rendezvous (so completa quando o helper le) — um helper que pare de ler
                // bloquearia o WriteLine de SendConfig para sempre.
                server = NamedPipeServerStreamAcl.Create(
                    $"monitoragent.{_sessionId}",
                    PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, BuildSecurity());

                await server.WaitForConnectionAsync(ct);
                _log.Info($"Helper conectado ao pipe da sessão {_sessionId}.");

                using var reader = new StreamReader(server, Encoding.UTF8, false, 16 * 1024, leaveOpen: true);
                lock (_writerGate)
                {
                    _writer = new StreamWriter(server, new UTF8Encoding(false), 16 * 1024, leaveOpen: true)
                    {
                        AutoFlush = true
                    };
                }

                SendConfig();

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break; // helper desconectou
                    HandleLine(line);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Error($"DACL/acesso negado no pipe da sessão {_sessionId}.", ex);
                _onPipeDenied();
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { return; }
            }
            catch (Exception ex)
            {
                _log.Error($"Falha no pipe da sessão {_sessionId} (reabrindo).", ex);
                _runtime.Errors.Report(ex, _sessionId); // F5: queda do IPC visível no portal, não só no log
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
                catch (OperationCanceledException) { return; }
            }
            finally
            {
                lock (_writerGate) { _writer = null; }
                server?.Dispose();
            }
        }
    }

    private PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        if (_userSid is not null)
        {
            security.AddAccessRule(new PipeAccessRule(_userSid,
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }
        return security;
    }

    private void HandleLine(string line)
    {
        PipeMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(line, AgentJsonContext.Default.PipeMessage);
        }
        catch (JsonException)
        {
            _log.Warn($"Mensagem ilegível no pipe da sessão {_sessionId} (descartada).");
            return;
        }
        if (message is null) return;
        if (_runtime.State.Unenrolled) return; // pós-UNENROLL: coleta parada

        switch (message.Kind)
        {
            case PipeMessage.KindEvent when message.Event is not null:
            {
                var ev = message.Event;
                if (ev.Type == EventTypes.Heartbeat)
                {
                    // o helper não conhece fila nem ack: o serviço injeta a saúde operacional
                    // (queue_depth, dead_letter_count, last_reject_code, working_set_mb, queue_db_bytes)
                    try
                    {
                        var hb = ev.Data.Deserialize(AgentJsonContext.Default.HeartbeatData);
                        if (hb is not null)
                        {
                            _runtime.EnrichHeartbeat(hb);
                            ev = ev.CloneWithData(EventFactory.ToElement(hb));
                        }
                    }
                    catch (JsonException) { /* heartbeat segue como veio */ }
                }
                _runtime.Queue.Enqueue(ev);
                break;
            }

            case PipeMessage.KindUpdate when message.Event is not null:
            {
                // anti-flapping N16: se o original já foi enviado, vira evento novo (id novo)
                if (!_runtime.Queue.TryUpdateUnsent(message.Event))
                {
                    var fresh = message.Event;
                    fresh.EventId = Uuid7.NewUuid7().ToString();
                    _runtime.Queue.Enqueue(fresh);
                }
                break;
            }

            case PipeMessage.KindDrops when message.Count is not null:
            {
                // reason da lista fechada DropReasons: rate_limit (coalescimento N17) ou
                // pipe_overflow (buffer volátil do helper cheio). Helper antigo não manda reason:
                // o default segue rate_limit, o único que ele reportava.
                var reason = DropReasons.IsKnown(message.Reason) ? message.Reason! : DropReasons.RateLimit;
                _runtime.Queue.Enqueue(_runtime.Factory.Create(EventTypes.EventsDropped,
                    new EventsDroppedData
                    {
                        Count = message.Count.Value,
                        OldestDroppedAt = message.OldestDroppedAt,
                        Reason = reason
                    }, _sessionId));
                if (reason == DropReasons.PipeOverflow)
                {
                    _log.Warn($"Helper da sessão {_sessionId} descartou {message.Count.Value} mensagem(ns) por " +
                              $"transbordo do buffer volátil (pipe_overflow) — EVENTS_DROPPED emitido.");
                }
                break;
            }

            case PipeMessage.KindDiagnosticsRequest:
                // o usuário pediu no tray: empacota e envia daqui (o token e os logs são do serviço)
                _log.Info($"Diagnóstico solicitado pelo tray da sessão {_sessionId} — empacotando e enviando…");
                _ = Task.Run(async () =>
                {
                    var ok = await _runtime.Diagnostics.UploadAsync(_cts.Token);
                    SendDiagnosticsResult(ok);
                });
                break;
        }
    }

    /// <summary>serviço → helper: resultado do envio de diagnóstico (balão do tray).</summary>
    public void SendDiagnosticsResult(bool ok)
    {
        lock (_writerGate)
        {
            if (_writer is null) return;
            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(
                    new PipeMessage { Kind = PipeMessage.KindDiagnosticsResult, Ok = ok },
                    AgentJsonContext.Default.PipeMessage));
            }
            catch (Exception ex)
            {
                _log.Warn($"Falha ao devolver o resultado do diagnóstico à sessão {_sessionId}: {ex.Message}");
            }
        }
    }

    /// <summary>serviço → helper: config aplicável + device_id + boot_id + último envio.</summary>
    public void SendConfig()
    {
        lock (_writerGate)
        {
            if (_writer is null) return;
            try
            {
                var message = new PipeMessage
                {
                    Kind = PipeMessage.KindConfig,
                    Config = _runtime.State.Config,
                    ConfigVersion = _runtime.State.ConfigVersion,
                    DeviceId = _runtime.State.DeviceId,
                    BootId = _runtime.Factory.BootId,
                    LastSentAt = _runtime.Sender.LastSuccessfulSendAt is { } t ? Iso.Format(t) : null,
                    ConnectionState = ConnectionStateNames.ToWire(ResolveConnectionState())
                };
                _writer.WriteLine(JsonSerializer.Serialize(message, AgentJsonContext.Default.PipeMessage));
            }
            catch (Exception ex)
            {
                _log.Warn($"Falha ao enviar config pelo pipe da sessão {_sessionId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Estado de conexao reportado ao helper (tray). Em regime o BatchSender e a fonte; mas antes do
    /// enroll de primeiro boot (golden image / NOENROLL) o BatchSender retorna cedo com NaoEnrolado
    /// (sem device_token) e nunca classifica o transporte. Se o enroll FALHOU por erro de certificado
    /// (possivel MITM, Secao 6.4 l.445), o EnrollmentClient registrou ErroCertificado em
    /// LastConnectionState — a reconciliacao o prefere, evitando que o tray mostre "dispositivo ainda
    /// nao registrado" e mascare justamente o cenario que a fatia cobre.
    /// </summary>
    private AgentConnectionState ResolveConnectionState() =>
        ConnectionStateNames.Reconcile(_runtime.Sender.ConnectionState, _runtime.Enrollment.LastConnectionState);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

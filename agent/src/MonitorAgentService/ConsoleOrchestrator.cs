using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using M351.Agent.Core.Collectors;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Win32;
using Microsoft.Win32;

namespace MonitorAgentService;

/// <summary>
/// MODO CONSOLE: roda TUDO num processo só na sessão atual — sem service control e sem
/// CreateProcessAsUser. Coletores in-process; eventos de sessão/energia aproximados via
/// SystemEvents; logs no stdout; parada limpa em Ctrl+C (e --run-for N para encerramento
/// programado em testes).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ConsoleOrchestrator
{
    public static async Task<int> RunAsync(int? runForSeconds)
    {
        var log = new ConsoleLogSink();
        log.Info("+351 Monitor — agente em MODO CONSOLE (processo único na sessão atual).");

        using var runtime = AgentRuntime.Create(log);
        log.Info($"Fila local (SQLite WAL): {runtime.Queue.DbPath}");
        log.Info($"Eventos na fila: {runtime.Queue.TotalCount} (pendentes de envio: {runtime.Queue.UnsentCount}).");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            log.Info("Ctrl+C recebido — encerrando com parada limpa…");
            cts.Cancel();
        };
        if (runForSeconds is int seconds)
        {
            log.Info($"Execução programada: encerramento automático em {seconds} s (--run-for).");
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        }

        runtime.EmitAgentStart();

        if (runtime.State.IsEnrolled)
        {
            log.Info($"Device registrado: device_id={runtime.State.DeviceId} | servidor={runtime.State.ServerUrl} " +
                     $"| config v{runtime.State.ConfigVersion}.");
        }
        else
        {
            log.Warn("Device NÃO registrado: os eventos serão acumulados OFFLINE na fila local (buffer N8).");
            log.Warn("Para registrar: MonitorAgentService.exe --enroll <ek_...> --server <url>");
        }

        // Identidade da sessão atual (no serviço real isso vem do WTS; aqui é a própria sessão)
        var identity = new SessionIdentity(
            Process.GetCurrentProcess().SessionId,
            WindowsIdentity.GetCurrent().User?.Value,
            $"{Environment.UserDomainName}\\{Environment.UserName}");

        // Aproximação do SESSION_START (no serviço real: SERVICE_CONTROL_SESSIONCHANGE)
        runtime.Queue.Enqueue(runtime.Factory.Create(EventTypes.SessionStart,
            new SessionStartData { LogonType = "console" },
            identity.SessionId, identity.WindowsSid, identity.WindowsUser));
        log.Info($"SESSION_START emitido (sessão {identity.SessionId}, aproximação do modo console).");

        using var systemEvents = new ConsoleSystemEventsMonitor(runtime, identity, log);

        var sink = new QueueEventSink(runtime.Queue, runtime.Factory, identity,
            () => runtime.Sender.LastSuccessfulSendAt);

        var engine = new SessionCollectorEngine(
            new Win32ForegroundWindowQuery(),
            new Win32IdleTimeQuery(),
            sink,
            runtime.Factory,
            identity,
            () => runtime.State.Config,
            () => systemEvents.IsLocked,
            () => runtime.Queue.UnsentCount,
            log);

        runtime.AckProcessor.ConfigApplied += cfg =>
        {
            engine.ApplyConfig(cfg);
            log.Info($"Coletores reconfigurados (idle_threshold={cfg.IdleThresholdSec}s, " +
                     $"poll={cfg.ActiveWindowPollSec}s, heartbeat={cfg.HeartbeatSec}s, política={cfg.WindowTitlePolicy}).");
        };

        using var collectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        runtime.AckProcessor.Unenrolled += () =>
        {
            log.Warn("UNENROLL processado: coleta interrompida, fila descartada, token esquecido.");
            collectCts.Cancel();
        };

        log.Info($"Coletores ativos: janela ativa (poll {runtime.State.Config.ActiveWindowPollSec}s, dedupe N1), " +
                 $"ociosidade (limiar {runtime.State.Config.IdleThresholdSec}s, N4), " +
                 $"heartbeat ({runtime.State.Config.HeartbeatSec}s, N2).");
        log.Info($"Envio em lote: a cada 30s ou 500 eventos (N3), retry com backoff exponencial + jitter (N14).");

        var tasks = new[]
        {
            engine.RunAsync(collectCts.Token),
            runtime.Sender.RunAsync(cts.Token),
            runtime.TimeMonitorLoopAsync(cts.Token)
        };

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { /* parada limpa */ }

        // Encerramento limpo
        if (!runtime.State.Unenrolled)
        {
            runtime.Queue.Enqueue(runtime.Factory.Create(EventTypes.SessionEnd, null,
                identity.SessionId, identity.WindowsSid, identity.WindowsUser));
        }
        runtime.EmitAgentStop("service_stop");
        await runtime.TryFlushAsync(TimeSpan.FromSeconds(5));

        log.Info($"Encerrado. Fila local: {runtime.Queue.TotalCount} eventos no total " +
                 $"({runtime.Queue.UnsentCount} pendentes de envio).");
        return 0;
    }
}

/// <summary>
/// Aproximações do modo console para eventos de sessão/energia (no serviço real vêm de
/// SERVICE_CONTROL_SESSIONCHANGE / SERVICE_CONTROL_POWEREVENT): LOCK/UNLOCK via
/// SystemEvents.SessionSwitch e SYSTEM_SUSPEND/RESUME via SystemEvents.PowerModeChanged.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ConsoleSystemEventsMonitor : IDisposable
{
    private readonly AgentRuntime _runtime;
    private readonly SessionIdentity _identity;
    private readonly ILogSink _log;
    private DateTimeOffset? _suspendedAt;

    public bool IsLocked { get; private set; }

    public ConsoleSystemEventsMonitor(AgentRuntime runtime, SessionIdentity identity, ILogSink log)
    {
        _runtime = runtime;
        _identity = identity;
        _log = log;
        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            log.Warn($"SystemEvents indisponível nesta sessão ({ex.GetType().Name}): LOCK/UNLOCK/SUSPEND não serão aproximados.");
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        try
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                    IsLocked = true;
                    Emit(EventTypes.Lock);
                    _log.Info("Sessão bloqueada — LOCK emitido.");
                    break;
                case SessionSwitchReason.SessionUnlock:
                    IsLocked = false;
                    Emit(EventTypes.Unlock);
                    _log.Info("Sessão desbloqueada — UNLOCK emitido.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Falha ao tratar SessionSwitch.", ex);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        try
        {
            if (e.Mode == PowerModes.Suspend)
            {
                _suspendedAt = DateTimeOffset.UtcNow;
                Emit(EventTypes.SystemSuspend);
                _log.Info("Suspensão — SYSTEM_SUSPEND emitido.");
            }
            else if (e.Mode == PowerModes.Resume && _suspendedAt is not null)
            {
                var sleepMs = (long)(DateTimeOffset.UtcNow - _suspendedAt.Value).TotalMilliseconds;
                _suspendedAt = null;
                Emit(EventTypes.SystemResume, new SystemResumeData { SleepDurationMs = Math.Max(sleepMs, 0) });
                _log.Info($"Retomada — SYSTEM_RESUME emitido (sleep_duration_ms={sleepMs}).");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Falha ao tratar PowerModeChanged.", ex);
        }
    }

    private void Emit(string type, object? payload = null)
    {
        if (_runtime.State.Unenrolled) return;
        _runtime.Queue.Enqueue(_runtime.Factory.Create(type, payload,
            _identity.SessionId, _identity.WindowsSid, _identity.WindowsUser));
    }

    public void Dispose()
    {
        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch (Exception) { /* best-effort */ }
    }
}

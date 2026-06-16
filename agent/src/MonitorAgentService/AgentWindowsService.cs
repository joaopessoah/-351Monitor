using System.Runtime.Versioning;
using System.ServiceProcess;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Logging;

namespace MonitorAgentService;

/// <summary>
/// Serviço Windows (LocalSystem, Session 0). Trata SERVICE_CONTROL_SESSIONCHANGE
/// (SESSION_START/END, LOCK, UNLOCK — Seção 6.1), SERVICE_CONTROL_POWEREVENT
/// (SYSTEM_SUSPEND/RESUME) e shutdown. Lança 1 MonitorAgentSession por sessão interativa.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentWindowsService : ServiceBase
{
    private AgentRuntime? _runtime;
    private SessionManager? _sessions;
    private CancellationTokenSource? _cts;
    private ILogSink _log = new NullLogSink();
    private DateTimeOffset? _suspendedAt;
    private string? _dataDir;

    public AgentWindowsService()
    {
        ServiceName = "MonitorAgentService";
        CanHandleSessionChangeEvent = true; // SERVICE_ACCEPT_SESSIONCHANGE
        CanHandlePowerEvent = true;         // SERVICE_CONTROL_POWEREVENT
        CanShutdown = true;
        CanStop = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        var dataDir = AgentRuntime.ResolveDataDirectory(new NullLogSink());
        _log = new FileLogSink(Path.Combine(dataDir, "logs"), "service");
        _log.Info("Serviço iniciando…");

        _dataDir = dataDir;

        // Higiene de sentinela: um start normal NUNCA e um uninstall. Se uma sentinela .uninstall
        // sobrou de um ciclo anterior (ex.: o servico foi parado sem passar pelo OnStop, ou o
        // uninstall foi abortado apos a sentinela ser gravada), descarta-la aqui evita que um stop
        // futuro reporte AGENT_STOP{uninstall} incorretamente apos um reinstall.
        if (UninstallFlag.Consume(dataDir, _log))
            _log.Warn("Sentinela de uninstall orfa encontrada no start — descartada (start nao e uninstall).");

        _runtime = AgentRuntime.Create(_log, dataDir);
        _cts = new CancellationTokenSource();
        _sessions = new SessionManager(_runtime, _log);

        // F4.1: aplica config do MSI (SERVERURL/PROXYURL) e, em golden image (NOENROLL=1),
        // faz o enroll no primeiro boot real com a key pendente gravada pelo instalador.
        ApplyInstallConfigAndEnroll(dataDir);

        // Task.Run: o loop de envio do BatchSender nunca pode bloquear em IPC — um push
        // sincrono de config ao helper (pipe) congelou a cadencia de 30 s no aceite F1
        // quando o read-loop do helper estava preso no NoticeForm modal.
        _runtime.AckProcessor.ConfigApplied += _ => Task.Run(() =>
        {
            try { _sessions?.BroadcastConfig(); }
            catch (Exception ex) { _log?.Warn($"Falha no broadcast de config aos helpers: {ex.Message}"); }
        });
        _runtime.AckProcessor.Unenrolled += () =>
        {
            _log.Warn("UNENROLL: parando helpers e coleta.");
            _sessions.StopAll();
        };

        _runtime.EmitAgentStart();
        _sessions.Start();

        var ct = _cts.Token;
        _ = _runtime.Sender.RunAsync(ct);
        _ = _runtime.TimeMonitorLoopAsync(ct);
        _ = MachineHeartbeatLoopAsync(ct);

        _log.Info("Serviço iniciado.");
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        if (_runtime is null || _sessions is null) return;
        var sessionId = change.SessionId;
        try
        {
            switch (change.Reason)
            {
                case SessionChangeReason.SessionLogon:
                    EmitSessionEvent(EventTypes.SessionStart, sessionId, new SessionStartData
                    {
                        // WTSClientProtocolType: 0 = console, 2 = rdp
                        LogonType = ServiceNativeMethods.QuerySessionProtocol(sessionId) == 2 ? "rdp" : "console"
                    });
                    _sessions.EnsureHelper(sessionId);
                    break;

                case SessionChangeReason.SessionLogoff:
                    EmitSessionEvent(EventTypes.SessionEnd, sessionId, null);
                    _sessions.Remove(sessionId);
                    break;

                case SessionChangeReason.SessionLock:
                    EmitSessionEvent(EventTypes.Lock, sessionId, null);
                    break;

                case SessionChangeReason.SessionUnlock:
                    EmitSessionEvent(EventTypes.Unlock, sessionId, null);
                    break;

                case SessionChangeReason.ConsoleConnect:
                case SessionChangeReason.RemoteConnect:
                    _sessions.EnsureHelper(sessionId);
                    break;

                // Disconnect (RDP/FUS): o helper pausa a coleta de janela sozinho; logoff fecha a sessão
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Falha ao tratar mudança de sessão ({change.Reason}, sessão {sessionId}).", ex);
        }
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus status)
    {
        if (_runtime is null) return true;
        try
        {
            switch (status)
            {
                case PowerBroadcastStatus.Suspend:
                    _suspendedAt = DateTimeOffset.UtcNow;
                    _runtime.Queue.Enqueue(_runtime.Factory.Create(EventTypes.SystemSuspend));
                    _log.Info("SYSTEM_SUSPEND emitido.");
                    break;

                case PowerBroadcastStatus.ResumeSuspend:
                case PowerBroadcastStatus.ResumeAutomatic:
                    if (_suspendedAt is not null)
                    {
                        var sleepMs = (long)(DateTimeOffset.UtcNow - _suspendedAt.Value).TotalMilliseconds;
                        _suspendedAt = null;
                        _runtime.Queue.Enqueue(_runtime.Factory.Create(EventTypes.SystemResume,
                            new SystemResumeData { SleepDurationMs = Math.Max(sleepMs, 0) }));
                        _log.Info($"SYSTEM_RESUME emitido (sleep_duration_ms={sleepMs}).");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Falha ao tratar evento de energia.", ex);
        }
        return true;
    }

    protected override void OnStop() => ShutdownCore(ResolveStopReason());

    protected override void OnShutdown() => ShutdownCore("shutdown");

    /// <summary>
    /// Distingue um stop de DESINSTALACAO de um stop normal do SCM (Secao 6.6). O MSI grava a
    /// sentinela .uninstall em %ProgramData% antes de mandar o SCM parar o servico no uninstall;
    /// sem ela, um stop e service_stop comum (parada manual, reboot agendado etc.).
    /// </summary>
    private string ResolveStopReason()
    {
        if (_dataDir is not null && UninstallFlag.Consume(_dataDir, _log))
        {
            _log.Info("Sentinela de uninstall detectada — AGENT_STOP sera emitido com reason=uninstall.");
            return "uninstall";
        }
        return "service_stop";
    }

    /// <summary>
    /// F4.1 — entrega do instalador. Persiste SERVERURL/PROXYURL do install.json onde o agente os
    /// le (State.ServerUrl) e, se houver enrollment key pendente (golden image / NOENROLL=1) e o
    /// device ainda nao estiver registrado, faz o enroll no primeiro boot real. Best-effort: falha
    /// de rede aqui nao impede o servico de subir — o re-enroll (N15) e o boot seguinte reentram.
    /// </summary>
    private void ApplyInstallConfigAndEnroll(string dataDir)
    {
        if (_runtime is null) return;
        var cfg = InstallConfig.TryLoad(dataDir, _log);
        if (cfg is null) return;

        if (!string.IsNullOrWhiteSpace(cfg.ServerUrl) && _runtime.State.ServerUrl is null)
        {
            _runtime.State.ServerUrl = cfg.ServerUrl;
            _log.Info($"SERVERURL do instalador aplicado: {cfg.ServerUrl}.");
        }

        var pendingKey = cfg.PendingEnrollKey;
        if (string.IsNullOrWhiteSpace(pendingKey) || _runtime.State.IsEnrolled) return;

        var serverUrl = cfg.ServerUrl ?? _runtime.State.ServerUrl;
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            _log.Warn("Enroll de primeiro boot pulado: sem SERVERURL no install.json.");
            return;
        }

        try
        {
            _log.Info("Golden image: enroll no primeiro boot real…");
            var ok = _runtime.Enrollment.EnrollAsync(serverUrl, pendingKey!, _cts?.Token ?? CancellationToken.None)
                .GetAwaiter().GetResult();
            if (ok)
            {
                // Consome a key pendente: o re-enroll futuro (N15) usa a enrollment key cifrada na fila.
                (cfg with { PendingEnrollKey = null }).Save(dataDir, _log);
                _log.Info("Enroll de primeiro boot concluido; key pendente removida do install.json.");
            }
            else
            {
                _log.Warn("Enroll de primeiro boot falhou — sera retentado no proximo boot/re-enroll.");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Falha no enroll de primeiro boot.", ex);
        }
    }

    private void ShutdownCore(string reason)
    {
        try
        {
            _log.Info($"Encerrando (reason={reason})…");
            _sessions?.StopAll();
            _cts?.Cancel();
            if (_runtime is not null)
            {
                _runtime.EmitAgentStop(reason);
                _runtime.TryFlushAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                _runtime.Dispose();
                _runtime = null;
            }
            _log.Info("Encerrado.");
        }
        catch (Exception ex)
        {
            _log.Error("Falha no encerramento.", ex);
        }
    }

    /// <summary>HEARTBEAT de máquina: state no_session quando não há sessão interativa (Seção 5.3).</summary>
    private async Task MachineHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_runtime is not null && !_runtime.State.Unenrolled && !HasActiveInteractiveSession())
                {
                    _runtime.Queue.Enqueue(_runtime.Factory.Create(EventTypes.Heartbeat, new HeartbeatData
                    {
                        State = "no_session",
                        ForegroundProcess = null,
                        IdleMs = null,
                        QueueDepth = _runtime.Queue.UnsentCount
                    }));
                }
            }
            catch (Exception ex)
            {
                _log.Error("Falha no heartbeat de máquina.", ex);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_runtime?.State.Config.HeartbeatSec ?? 60), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static bool HasActiveInteractiveSession()
    {
        foreach (var (sessionId, state) in ServiceNativeMethods.EnumerateSessions())
        {
            if (state == ServiceNativeMethods.WTSActive && sessionId != 0) return true;
        }
        return false;
    }

    private void EmitSessionEvent(string type, int sessionId, object? payload)
    {
        if (_runtime is null || _runtime.State.Unenrolled) return;
        var domain = ServiceNativeMethods.QuerySessionString(sessionId, ServiceNativeMethods.WTSDomainName);
        var user = ServiceNativeMethods.QuerySessionString(sessionId, ServiceNativeMethods.WTSUserName);
        var windowsUser = user is null ? null : $"{domain ?? Environment.MachineName}\\{user}";
        _runtime.Queue.Enqueue(_runtime.Factory.Create(type, payload, sessionId,
            _sessions?.GetSessionSid(sessionId), windowsUser));
        _log.Info($"{type} emitido (sessão {sessionId}).");
    }
}

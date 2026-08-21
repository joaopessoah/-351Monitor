using System.Runtime.Versioning;
using System.ServiceProcess;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Update;

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
    private IDisposable? _logDisposable;
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

        // Serilog em %ProgramData%\M351\MonitorAgent\logs\service-.log: rotacao diaria, 5 MB/arquivo,
        // maximo 10 arquivos (Secao 6.6 l.461). verbose_debug (install.json) habilita o nivel Debug.
        var verboseDebug = InstallConfig.TryLoad(dataDir, new NullLogSink())?.VerboseDebug ?? false;
        var serilog = SerilogLogSink.CreateFile(Path.Combine(dataDir, "logs"), "service", verboseDebug);
        _logDisposable = serilog;
        _log = serilog;
        _log.Info("Serviço iniciando…");
        if (verboseDebug)
            _log.Warn("Log em nivel Debug ATIVADO (verbose_debug): detalhe sensivel (titulo/usuario) pode " +
                      "aparecer apenas nos arquivos de log Debug. Desative quando nao estiver diagnosticando.");

        _dataDir = dataDir;

        // Auto-update (Secao 6.7): a sentinela .update foi gravada pelo agente antes do msiexec; o
        // MSI reinstalou e religou o servico. Consumi-la AQUI (uma vez, antes de criar o runtime)
        // faz o AGENT_START sair com start_reason "update" (precede crash_recovery/boot). Se nao ha
        // sentinela, e um start normal. Consumir tambem evita herdar uma sentinela orfa num start futuro.
        var updateDetected = UpdateFlag.Consume(dataDir, _log);
        if (updateDetected)
            _log.Info("Sentinela .update detectada no start — start_reason sera update.");

        // Higiene de sentinela: um start normal NUNCA e um uninstall. Se uma sentinela .uninstall
        // sobrou de um ciclo anterior (ex.: o servico foi parado sem passar pelo OnStop, ou o
        // uninstall foi abortado apos a sentinela ser gravada), descarta-la aqui evita que um stop
        // futuro reporte AGENT_STOP{uninstall} incorretamente apos um reinstall.
        if (UninstallFlag.Consume(dataDir, _log))
            _log.Warn("Sentinela de uninstall orfa encontrada no start — descartada (start nao e uninstall).");

        _runtime = AgentRuntime.Create(_log, dataDir, updateDetected);
        _cts = new CancellationTokenSource();
        _sessions = new SessionManager(_runtime, _log);

        // F4.1: aplica config do MSI (SERVERURL) de forma síncrona — só I/O local, sem rede,
        // para o BatchSender já nascer com State.ServerUrl. O enroll de golden image (rede)
        // é disparado em background ao FINAL do OnStart (ver FirstBootEnrollWithRetryAsync).
        ApplyInstallConfig(dataDir);

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
        _ = StartUpdateLoop(dataDir, ct);

        // F4.1 (golden image): o enroll de primeiro boot NUNCA roda dentro do OnStart — a versão
        // síncrona anterior bloqueava em EnrollAsync com HttpClient de timeout 30 s e arriscava o
        // erro 1053 do SCM no primeiro boot com DNS/proxy lento. Best-effort por contrato: falha
        // aqui não impede o serviço de subir.
        _ = Task.Run(() => FirstBootEnrollWithRetryAsync(dataDir, ct), ct);

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
    /// Distingue stops especiais de um stop normal do SCM (Secao 6.6 / 6.7). Precedencia:
    /// uninstall &gt; update &gt; service_stop.
    ///   - .uninstall (gravada pelo MSI no uninstall): AGENT_STOP{reason:"uninstall"} — consumida aqui.
    ///   - .update (gravada pelo agente antes do msiexec): AGENT_STOP{reason:"update"}; NAO consumir
    ///     aqui — o OnStart do servico religado precisa ve-la para start_reason "update".
    ///   - sem sentinela: service_stop (parada manual, reboot agendado etc.).
    /// </summary>
    private string ResolveStopReason()
    {
        if (_dataDir is not null && UninstallFlag.Consume(_dataDir, _log))
        {
            _log.Info("Sentinela de uninstall detectada — AGENT_STOP sera emitido com reason=uninstall.");
            return "uninstall";
        }
        if (_dataDir is not null && UpdateFlag.IsSet(_dataDir))
        {
            _log.Info("Sentinela .update detectada — AGENT_STOP sera emitido com reason=update (sentinela preservada para o start).");
            return "update";
        }
        return "service_stop";
    }

    /// <summary>
    /// F4.1 — entrega do instalador (parte síncrona, só I/O local). Persiste SERVERURL do
    /// install.json onde o agente o lê (State.ServerUrl). O PROXYURL é consumido pelo
    /// AgentRuntime no ponto único do HttpClient; o enroll de golden image (rede) fica em
    /// FirstBootEnrollWithRetryAsync, fora do caminho crítico do OnStart.
    /// </summary>
    private void ApplyInstallConfig(string dataDir)
    {
        if (_runtime is null) return;
        var cfg = InstallConfig.TryLoad(dataDir, _log);
        if (cfg is null) return;

        if (!string.IsNullOrWhiteSpace(cfg.ServerUrl) && _runtime.State.ServerUrl is null)
        {
            _runtime.State.ServerUrl = cfg.ServerUrl;
            _log.Info($"SERVERURL do instalador aplicado: {cfg.ServerUrl}.");
        }
    }

    /// <summary>
    /// F4.1 — enroll de golden image (NOENROLL=1) no primeiro boot real, em background com retry
    /// exponencial curto (imediato, depois esperas de 5 s / 15 s / 45 s) e desistência logada.
    /// Best-effort por contrato: falha aqui não impede o serviço de subir. Depois de desistir, a
    /// key pendente continua no install.json e o próximo boot reentra; para device já enrolado
    /// com token revogado (401), o re-enroll horário N15 do BatchSender cobre.
    ///
    /// Concorrência: é seguro o enroll terminar com o serviço já coletando. Até EnrollAsync
    /// persistir device_token/device_id, State.IsEnrolled é false e o BatchSender apenas acumula
    /// eventos na fila (TickAsync não envia); MaybeReenrollAsync (N15) não dispara em paralelo
    /// porque State.EnrollmentKey só é persistida pelo próprio EnrollAsync no sucesso. As escritas
    /// de estado são serializadas pelo lock da fila SQLite (SqliteEventQueue.KvSet). Após o
    /// sucesso, o sender passa a enviar no tick seguinte; um 401 posterior segue coberto pelo N15
    /// (o BatchSender trata 401 mantendo a fila e re-enrolando).
    /// </summary>
    private async Task FirstBootEnrollWithRetryAsync(string dataDir, CancellationToken ct)
    {
        if (_runtime is null) return;
        var cfg = InstallConfig.TryLoad(dataDir, _log);
        var pendingKey = cfg?.PendingEnrollKey;
        if (cfg is null || string.IsNullOrWhiteSpace(pendingKey) || _runtime.State.IsEnrolled) return;

        var serverUrl = cfg.ServerUrl ?? _runtime.State.ServerUrl;
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            _log.Warn("Enroll de primeiro boot pulado: sem SERVERURL no install.json.");
            return;
        }

        TimeSpan[] delays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45)];
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _log.Info($"Golden image: enroll no primeiro boot real (tentativa {attempt + 1} de {delays.Length + 1})…");
                if (await _runtime.Enrollment.EnrollAsync(serverUrl, pendingKey!, ct))
                {
                    // Consome a key pendente: o re-enroll futuro (N15) usa a enrollment key cifrada na fila.
                    (cfg with { PendingEnrollKey = null }).Save(dataDir, _log);
                    _log.Info("Enroll de primeiro boot concluído; key pendente removida do install.json.");
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // serviço parando: o próximo boot reentra
            }
            catch (Exception ex)
            {
                _log.Error("Falha no enroll de primeiro boot.", ex);
            }

            if (attempt >= delays.Length)
            {
                _log.Warn($"Enroll de primeiro boot desistiu após {delays.Length + 1} tentativas — " +
                          "key pendente preservada; o próximo boot reentra e o re-enroll N15 cobre device já enrolado.");
                return;
            }

            try { await Task.Delay(delays[attempt], ct); }
            catch (OperationCanceledException) { return; }
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
        finally
        {
            // Garante o flush do buffer do Serilog antes do processo morrer.
            _logDisposable?.Dispose();
            _logDisposable = null;
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

    /// <summary>
    /// Auto-update (Secao 6.7): checagem a cada 6 h (jitter ate 30 min). O installer grava a
    /// sentinela .update (consumida no proximo OnStart -> start_reason "update") e dispara
    /// msiexec /i /qn, que para este servico (ResolveStopReason -> reason "update").
    /// </summary>
    private async Task StartUpdateLoop(string dataDir, CancellationToken ct)
    {
        if (_runtime is null) return;
        var updatesDir = Path.Combine(dataDir, "updates");
        var installer = new UpdateInstaller(
            _runtime.UpdateClient, _log, updatesDir,
            writeUpdateSentinel: () => UpdateFlag.Write(dataDir, _log),
            clearUpdateSentinel: () => UpdateFlag.Consume(dataDir, _log));
        var service = new UpdateService(_runtime.UpdateClient, installer, _runtime.State, _log);
        try
        {
            await service.RunAsync(ct);
        }
        catch (Exception ex)
        {
            _log.Error("Loop de auto-update encerrado por excecao.", ex);
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

        // SESSION_START chega ANTES de EnsureHelper criar o host (o SID via token do helper ainda
        // não existe e GetSessionSid retorna null). Fallback: resolve o SID do próprio windows_user
        // com LookupAccountName (SessionSidResolver); se também falhar, o evento sai sem windows_sid.
        var windowsSid = _sessions?.GetSessionSid(sessionId) ?? SessionSidResolver.TryResolve(windowsUser);

        _runtime.Queue.Enqueue(_runtime.Factory.Create(type, payload, sessionId, windowsSid, windowsUser));
        _log.Info($"{type} emitido (sessão {sessionId}).");
    }
}

using M351.Agent.Core;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Events;
using M351.Agent.Core.Fingerprint;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Net;
using M351.Agent.Core.Queue;
using M351.Agent.Core.Security;
using M351.Agent.Core.Storage;
using M351.Agent.Core.Time;
using M351.Agent.Core.Update;
using M351.Agent.Core.Win32;

namespace MonitorAgentService;

/// <summary>
/// Núcleo compartilhado entre o modo serviço e o modo console: fila SQLite (WAL), estado
/// persistente (DPAPI), fábrica de envelopes, sender HTTP e detecção de mudança de relógio.
/// </summary>
public sealed class AgentRuntime : IDisposable
{
    public SqliteEventQueue Queue { get; }
    public AgentStateStore State { get; }
    public EventFactory Factory { get; }
    public AckProcessor AckProcessor { get; }
    public BatchSender Sender { get; }
    public EnrollmentClient Enrollment { get; }
    public UpdateClient UpdateClient { get; }
    public ILogSink Log { get; }
    public string DataDirectory { get; }
    public string StartReason { get; }

    private readonly HttpClient _http;

    private AgentRuntime(string dataDirectory, ILogSink log, bool updateDetected, string? proxyUrl)
    {
        DataDirectory = dataDirectory;
        Log = log;

        Queue = new SqliteEventQueue(Path.Combine(dataDirectory, "queue.db"));
        State = new AgentStateStore(Queue, new DpapiSecretProtector());

        var (bootId, startReason) = State.InitializeBoot(DateTimeOffset.UtcNow, Environment.TickCount64, updateDetected);
        StartReason = startReason;
        Factory = new EventFactory(bootId);

        Queue.DropEventFactory = (count, oldest, reason) => Factory.Create(EventTypes.EventsDropped,
            new EventsDroppedData { Count = count, OldestDroppedAt = oldest, Reason = reason });
        Queue.Dropped += (count, reason) =>
            Log.Warn($"Buffer local (N8): {count} eventos expurgados FIFO ({reason}) — EVENTS_DROPPED emitido.");

        // Ponto unico de construcao do HttpClient (enroll/batch/update compartilham): proxy de
        // sistema (WinHTTP) por default; PROXYURL do install.json quando presente (Secao 6.4 l.445).
        _http = AgentHttpClientFactory.Create(proxyUrl, log);
        Enrollment = new EnrollmentClient(_http, State, new WindowsFingerprintSource(), log, SystemInventory.DescribeOs);
        AckProcessor = new AckProcessor(Queue, State, Factory, log);
        Sender = new BatchSender(_http, Queue, State, AckProcessor, Enrollment, log);
        UpdateClient = new UpdateClient(_http, State, log); // reusa HttpClient + device token
    }

    /// <summary>
    /// updateDetected: a sentinela .update foi vista (e consumida) neste start — o AGENT_START
    /// saira com start_reason "update" (precede crash_recovery/boot/service_restart). O modo
    /// console nunca passa por update (false).
    /// </summary>
    public static AgentRuntime Create(ILogSink log, string? dataDirOverride = null, bool updateDetected = false)
    {
        var dataDir = dataDirOverride ?? ResolveDataDirectory(log);
        // PROXYURL gravado pelo MSI no install.json (F4.1): consumido aqui no ponto unico do HttpClient.
        var proxyUrl = InstallConfig.TryLoad(dataDir, log)?.ProxyUrl;
        return new AgentRuntime(dataDir, log, updateDetected, proxyUrl);
    }

    /// <summary>
    /// %ProgramData%\M351\MonitorAgent (Seção 6.4); fallback %LOCALAPPDATA% quando o usuário
    /// do modo console não tem acesso (ex.: diretório criado antes pelo SYSTEM).
    /// </summary>
    public static string ResolveDataDirectory(ILogSink log)
    {
        var programData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "M351", "MonitorAgent");
        if (TryUseDirectory(programData)) return programData;

        var localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "M351", "MonitorAgent");
        log.Warn($"Sem acesso de escrita a {programData} — usando {localAppData}.");
        Directory.CreateDirectory(localAppData);
        return localAppData;
    }

    private static bool TryUseDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void EmitAgentStart()
    {
        var data = SystemInventory.BuildAgentStartData(Factory.BootId, StartReason, Environment.TickCount64);
        Queue.Enqueue(Factory.Create(EventTypes.AgentStart, data));
        Log.Info($"AGENT_START emitido (start_reason={StartReason}, boot_id={Factory.BootId}).");
    }

    /// <summary>reason: shutdown | service_stop | update | uninstall.</summary>
    public void EmitAgentStop(string reason)
    {
        if (State.Unenrolled) return; // pós-UNENROLL a fila fica vazia e descartada
        Queue.Enqueue(Factory.Create(EventTypes.AgentStop, new AgentStopData { Reason = reason }));
        State.MarkCleanShutdown();
        Log.Info($"AGENT_STOP emitido (reason={reason}); shutdown limpo registrado.");
    }

    /// <summary>Flush final best-effort (Seção 6.2: shutdown com flush da fila).</summary>
    public async Task TryFlushAsync(TimeSpan timeout)
    {
        if (!State.IsEnrolled) return;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            while (Queue.UnsentCount > 0 && !cts.IsCancellationRequested)
            {
                if (!await Sender.SendOnceAsync(cts.Token)) break;
            }
        }
        catch (OperationCanceledException) { /* melhor esforço */ }
    }

    /// <summary>Relógio: desvio wall-clock vs monotônico > 30 s → TIME_CHANGED (Seção 6.2).</summary>
    public async Task TimeMonitorLoopAsync(CancellationToken ct)
    {
        var detector = new TimeChangeDetector();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var change = detector.Sample(DateTimeOffset.UtcNow, Environment.TickCount64);
                if (change is not null)
                {
                    Queue.Enqueue(Factory.Create(EventTypes.TimeChanged, change));
                    Log.Warn($"Mudança de relógio detectada (delta {change.DeltaMs} ms) — TIME_CHANGED emitido.");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Falha no monitor de relógio.", ex);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        Queue.Dispose();
    }
}

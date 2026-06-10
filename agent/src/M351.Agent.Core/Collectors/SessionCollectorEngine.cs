using M351.Agent.Core.Contracts;
using M351.Agent.Core.Events;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Privacy;

namespace M351.Agent.Core.Collectors;

public sealed record SessionIdentity(int? SessionId, string? WindowsSid, string? WindowsUser);

public sealed record CollectorStatus(
    string? ForegroundProcess, string? ForegroundTitle, string State, long IdleMs, DateTimeOffset? LastSentAt);

/// <summary>
/// Coletores de sessão (Seção 6.2): janela ativa por polling 5 s com dedupe (N1),
/// ociosidade GetLastInputInfo a cada 5 s (N4/N5) e HEARTBEAT de sessão 60 s (N2).
/// Roda no helper (eventos via pipe) e no modo console (eventos direto na fila).
/// </summary>
public sealed class SessionCollectorEngine
{
    private readonly IForegroundWindowQuery _foreground;
    private readonly IIdleTimeQuery _idleQuery;
    private readonly IEventSink _sink;
    private readonly EventFactory _factory;
    private readonly SessionIdentity _identity;
    private readonly Func<AgentConfig> _getConfig;
    private readonly Func<bool> _isLocked;
    private readonly Func<long>? _queueDepth;
    private readonly ILogSink _log;

    public ActiveWindowTracker WindowTracker { get; }
    public IdleTracker IdleTracker { get; }

    private bool _outsideWindowLogged;

    public SessionCollectorEngine(
        IForegroundWindowQuery foreground,
        IIdleTimeQuery idleQuery,
        IEventSink sink,
        EventFactory factory,
        SessionIdentity identity,
        Func<AgentConfig> getConfig,
        Func<bool> isLocked,
        Func<long>? queueDepth,
        ILogSink log)
    {
        _foreground = foreground;
        _idleQuery = idleQuery;
        _sink = sink;
        _factory = factory;
        _identity = identity;
        _getConfig = getConfig;
        _isLocked = isLocked;
        _queueDepth = queueDepth;
        _log = log;

        WindowTracker = new ActiveWindowTracker(new TitleMasker(), getConfig());
        IdleTracker = new IdleTracker(getConfig().IdleThresholdSec);
    }

    public CollectorStatus Status
    {
        get
        {
            var data = WindowTracker.LastData;
            return new CollectorStatus(data?.ProcessName, data?.WindowTitle, CurrentState, IdleTracker.LastIdleMs, _sink.LastSentAt);
        }
    }

    private string CurrentState =>
        _isLocked() ? "locked" : IdleTracker.IsIdle ? "idle" : "active";

    public void ApplyConfig(AgentConfig config)
    {
        WindowTracker.UpdateConfig(config);
        IdleTracker.UpdateThreshold(config.IdleThresholdSec);
    }

    public Task RunAsync(CancellationToken ct) =>
        Task.WhenAll(WindowLoopAsync(ct), IdleLoopAsync(ct), HeartbeatLoopAsync(ct));

    private AgentEvent Create(string type, object? payload) =>
        _factory.Create(type, payload, _identity.SessionId, _identity.WindowsSid, _identity.WindowsUser);

    private bool CollectionAllowedNow()
    {
        var allowed = ScheduleWindow.IsCollectionAllowed(_getConfig().CollectionWindow, DateTime.Now);
        if (!allowed && !_outsideWindowLogged)
        {
            _log.Info("Fora da janela de coleta (BUSINESS_HOURS): janela ativa e ociosidade pausadas.");
            _outsideWindowLogged = true;
        }
        else if (allowed)
        {
            _outsideWindowLogged = false;
        }
        return allowed;
    }

    private async Task WindowLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_isLocked() && CollectionAllowedNow())
                {
                    var sample = _foreground.GetForegroundWindowInfo();
                    var result = WindowTracker.Sample(sample,
                        data => Create(EventTypes.ActiveWindowChanged, data));
                    if (result is not null)
                    {
                        if (result.Drops is not null)
                            _sink.ReportDrops(result.Drops.Count, result.Drops.OldestDroppedAtIso);
                        if (result.NewEvent is not null)
                            _sink.Emit(result.NewEvent);
                        else if (result.UpdatedEvent is not null)
                            _sink.Update(result.UpdatedEvent, result.UpdatedData!);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("Falha no loop de janela ativa (amostra ignorada).", ex);
            }

            await DelaySeconds(_getConfig().ActiveWindowPollSec, ct);
        }
    }

    private async Task IdleLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_isLocked() && CollectionAllowedNow())
                {
                    var transition = IdleTracker.Sample(_idleQuery.GetIdleMilliseconds());
                    if (transition is not null)
                    {
                        if (transition.Kind == IdleTransitionKind.Start)
                        {
                            _sink.Emit(Create(EventTypes.IdleStart,
                                new IdleStartData { LastInputAt = Iso.Format(transition.LastInputAt) }));
                            _log.Info("Ociosidade iniciada (IDLE_START retroativo ao último input).");
                        }
                        else
                        {
                            _sink.Emit(Create(EventTypes.IdleEnd,
                                new IdleEndData { IdleDurationMs = transition.IdleDurationMs }));
                            _log.Info($"Ociosidade encerrada após {transition.IdleDurationMs} ms (IDLE_END).");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("Falha no loop de ociosidade (amostra ignorada).", ex);
            }

            await DelaySeconds(5, ct); // verificação a cada 5 s (Seção 6.2)
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _sink.Emit(Create(EventTypes.Heartbeat, new HeartbeatData
                {
                    State = CurrentState,
                    ForegroundProcess = WindowTracker.LastData?.ProcessName,
                    IdleMs = IdleTracker.LastIdleMs,
                    QueueDepth = _queueDepth?.Invoke() ?? 0
                }));
            }
            catch (Exception ex)
            {
                _log.Error("Falha ao emitir HEARTBEAT.", ex);
            }

            await DelaySeconds(_getConfig().HeartbeatSec, ct);
        }
    }

    private static async Task DelaySeconds(int seconds, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(seconds, 1)), ct); }
        catch (OperationCanceledException) { /* parada limpa */ }
    }
}

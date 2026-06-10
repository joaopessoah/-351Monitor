using M351.Agent.Core.Contracts;
using M351.Agent.Core.Events;
using M351.Agent.Core.Privacy;

namespace M351.Agent.Core.Collectors;

/// <summary>Resultado de uma amostra do tracker (no máximo um dos campos de evento preenchido).</summary>
public sealed class TrackerResult
{
    /// <summary>Novo ACTIVE_WINDOW_CHANGED a enfileirar.</summary>
    public AgentEvent? NewEvent { get; init; }

    /// <summary>Anti-flapping N16: último evento atualizado (mesmo event_id/occurred_at, payload novo).</summary>
    public AgentEvent? UpdatedEvent { get; init; }

    /// <summary>Payload do update — fallback para reemissão caso o evento já tenha sido enviado.</summary>
    public ActiveWindowData? UpdatedData { get; init; }

    /// <summary>Coalescimentos pendentes do rate limit N17 (emitir EVENTS_DROPPED{rate_limit}).</summary>
    public DropReport? Drops { get; init; }
}

public sealed record DropReport(long Count, string OldestDroppedAtIso);

/// <summary>
/// Janela ativa por polling com dedupe (N1), anti-flapping de título (N16) e
/// rate limit de ACTIVE_WINDOW_CHANGED (N17: máx. 1/s e 600/h por sessão, excedente coalescido).
/// </summary>
public sealed class ActiveWindowTracker
{
    private const long AntiFlapWindowMs = 10_000; // N16
    private const long MinIntervalMs = 1_000;     // N17
    private const int MaxPerHour = 600;           // N17
    private const long HourMs = 3_600_000;

    private readonly TitleMasker _masker;
    private readonly Func<long> _monoMs;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Queue<long> _hourWindow = new();

    private volatile AgentConfig _config;
    private (string Process, string? Title)? _lastKey;
    private AgentEvent? _lastEvent;
    private ActiveWindowData? _lastData;
    private long? _lastEmitMono;
    private long _droppedCount;
    private string? _oldestDroppedIso;

    public ActiveWindowTracker(TitleMasker masker, AgentConfig config,
        Func<long>? monoMs = null, Func<DateTimeOffset>? utcNow = null)
    {
        _masker = masker;
        _config = config;
        _monoMs = monoMs ?? (() => Environment.TickCount64);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public ActiveWindowData? LastData => _lastData;

    public void UpdateConfig(AgentConfig config) => _config = config;

    /// <summary>
    /// Processa uma amostra de polling. Retorna null quando nada deve ser emitido
    /// (sem janela, dedupe ou excedente coalescido).
    /// </summary>
    public TrackerResult? Sample(ForegroundSample? sample, Func<ActiveWindowData, AgentEvent> createEvent)
    {
        if (sample is null) return null; // GetForegroundWindow NULL em trocas: ignorar sem crash

        var data = _masker.Apply(sample, _config);
        var key = (Process: data.ProcessName, Title: NormalizeTitle(data.WindowTitle));

        // Dedupe N1: só mudança de (process_name, título normalizado)
        if (_lastKey is not null && _lastKey.Value == key) return null;

        var nowMono = _monoMs();

        // Anti-flapping N16: mudança só de título (mesmo processo) em < 10 s → atualiza o último evento
        if (_lastEvent is not null && _lastData is not null && _lastEmitMono is not null &&
            data.ProcessName == _lastData.ProcessName &&
            nowMono - _lastEmitMono.Value < AntiFlapWindowMs)
        {
            var updated = _lastEvent.CloneWithData(EventFactory.ToElement(data));
            _lastEvent = updated;
            _lastData = data;
            _lastKey = key;
            return new TrackerResult { UpdatedEvent = updated, UpdatedData = data };
        }

        // Rate limit N17 (excedente coalescido: a próxima amostra permitida emite o estado corrente)
        PruneHourWindow(nowMono);
        if ((_lastEmitMono is not null && nowMono - _lastEmitMono.Value < MinIntervalMs) ||
            _hourWindow.Count >= MaxPerHour)
        {
            _droppedCount++;
            _oldestDroppedIso ??= Iso.Format(_utcNow());
            return null;
        }

        var ev = createEvent(data);
        DropReport? drops = null;
        if (_droppedCount > 0)
        {
            drops = new DropReport(_droppedCount, _oldestDroppedIso!);
            _droppedCount = 0;
            _oldestDroppedIso = null;
        }

        _lastKey = key;
        _lastEvent = ev;
        _lastData = data;
        _lastEmitMono = nowMono;
        _hourWindow.Enqueue(nowMono);

        return new TrackerResult { NewEvent = ev, Drops = drops };
    }

    private void PruneHourWindow(long nowMono)
    {
        while (_hourWindow.Count > 0 && nowMono - _hourWindow.Peek() >= HourMs)
            _hourWindow.Dequeue();
    }

    private static string? NormalizeTitle(string? title) => title?.Trim().ToLowerInvariant();
}

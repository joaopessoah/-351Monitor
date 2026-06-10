using M351.Agent.Core.Contracts;
using M351.Agent.Core.Events;
using M351.Agent.Core.Queue;

namespace M351.Agent.Core.Collectors;

/// <summary>Sink que grava direto na fila SQLite (modo console e eventos do próprio serviço).</summary>
public sealed class QueueEventSink : IEventSink
{
    private readonly SqliteEventQueue _queue;
    private readonly EventFactory _factory;
    private readonly SessionIdentity _identity;
    private readonly Func<DateTimeOffset?>? _lastSentAt;

    public QueueEventSink(SqliteEventQueue queue, EventFactory factory, SessionIdentity identity,
        Func<DateTimeOffset?>? lastSentAt = null)
    {
        _queue = queue;
        _factory = factory;
        _identity = identity;
        _lastSentAt = lastSentAt;
    }

    public DateTimeOffset? LastSentAt => _lastSentAt?.Invoke();

    public void Emit(AgentEvent ev) => _queue.Enqueue(ev);

    public void Update(AgentEvent updated, ActiveWindowData fallbackData)
    {
        if (!_queue.TryUpdateUnsent(updated))
        {
            // o evento original já foi enviado: emite um novo (idempotência preservada)
            _queue.Enqueue(_factory.Create(EventTypes.ActiveWindowChanged, fallbackData,
                _identity.SessionId, _identity.WindowsSid, _identity.WindowsUser));
        }
    }

    public void ReportDrops(long count, string oldestDroppedAtIso)
    {
        _queue.Enqueue(_factory.Create(EventTypes.EventsDropped, new EventsDroppedData
        {
            Count = count,
            OldestDroppedAt = oldestDroppedAtIso,
            Reason = "rate_limit"
        }));
    }
}

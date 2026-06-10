using M351.Agent.Core.Collectors;
using M351.Agent.Core.Contracts;

namespace MonitorAgentSession;

/// <summary>Sink do helper: tudo vai ao serviço pelo pipe (o helper não toca fila nem token).</summary>
public sealed class PipeEventSink : IEventSink
{
    private readonly PipeClient _pipe;

    public PipeEventSink(PipeClient pipe)
    {
        _pipe = pipe;
    }

    public DateTimeOffset? LastSentAt => _pipe.LastSentAt;

    public void Emit(AgentEvent ev) =>
        _pipe.Send(new PipeMessage { Kind = PipeMessage.KindEvent, Event = ev });

    public void Update(AgentEvent updated, ActiveWindowData fallbackData) =>
        _pipe.Send(new PipeMessage { Kind = PipeMessage.KindUpdate, Event = updated });

    public void ReportDrops(long count, string oldestDroppedAtIso) =>
        _pipe.Send(new PipeMessage
        {
            Kind = PipeMessage.KindDrops,
            Count = count,
            OldestDroppedAt = oldestDroppedAtIso
        });
}

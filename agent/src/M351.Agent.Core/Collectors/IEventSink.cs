using M351.Agent.Core.Contracts;

namespace M351.Agent.Core.Collectors;

/// <summary>
/// Destino dos eventos coletados: a fila SQLite (modo console / serviço) ou o named pipe (helper).
/// O helper JAMAIS acessa a fila ou o token — só envia mensagens ao serviço.
/// </summary>
public interface IEventSink
{
    void Emit(AgentEvent ev);

    /// <summary>Anti-flapping N16: tenta atualizar o último evento; se já enviado, reemite com payload novo.</summary>
    void Update(AgentEvent updated, ActiveWindowData fallbackData);

    /// <summary>Rate limit N17: reporta excedente coalescido (vira EVENTS_DROPPED{rate_limit}).</summary>
    void ReportDrops(long count, string oldestDroppedAtIso);

    /// <summary>Último envio ao servidor (exibição na janela de transparência).</summary>
    DateTimeOffset? LastSentAt { get; }
}

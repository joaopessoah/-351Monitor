using M351.Agent.Core.Contracts;
using M351.Agent.Core.Events;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Queue;
using M351.Agent.Core.Storage;

namespace M351.Agent.Core.Net;

/// <summary>
/// Processa o ack do batch — único canal de config e comandos (Seção 5.5):
/// config nova → aplica + persiste + emite POLICY_APPLIED{config_version};
/// UNENROLL → para a coleta, DESCARTA a fila local e esquece o token (idempotente se reentregue).
/// </summary>
public sealed class AckProcessor
{
    private readonly SqliteEventQueue _queue;
    private readonly AgentStateStore _state;
    private readonly EventFactory _factory;
    private readonly ILogSink _log;

    /// <summary>Disparado quando uma config nova foi aplicada (coletores devem reconfigurar).</summary>
    public event Action<AgentConfig>? ConfigApplied;

    /// <summary>Disparado no UNENROLL (orquestrador para a coleta).</summary>
    public event Action? Unenrolled;

    public AckProcessor(SqliteEventQueue queue, AgentStateStore state, EventFactory factory, ILogSink log)
    {
        _queue = queue;
        _state = state;
        _factory = factory;
        _log = log;
    }

    public void Process(AckResponse ack)
    {
        if (ack.Config is not null)
        {
            _state.SaveConfig(ack.Config, ack.ConfigVersion);
            _queue.Enqueue(_factory.Create(EventTypes.PolicyApplied,
                new PolicyAppliedData { ConfigVersion = ack.ConfigVersion }));
            _log.Info($"Config v{ack.ConfigVersion} aplicada (POLICY_APPLIED emitido).");
            ConfigApplied?.Invoke(ack.Config);
        }

        if (ack.Commands is null) return;
        foreach (var command in ack.Commands)
        {
            if (command.Type == CommandTypes.Unenroll)
            {
                _log.Warn("Comando UNENROLL recebido: parando coleta, descartando fila local e esquecendo o token.");
                _queue.ClearAll();
                _state.ForgetIdentity();
                Unenrolled?.Invoke();
            }
            else
            {
                // ROTATE_TOKEN / UPDATE_AGENT / PAUSE são v1.1 — ignorar sem rejeitar (compatibilidade)
                _log.Warn($"Comando desconhecido ignorado: {command.Type}");
            }
        }
    }
}

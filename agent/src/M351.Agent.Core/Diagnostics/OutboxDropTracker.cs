using M351.Agent.Core.Contracts;

namespace M351.Agent.Core.Diagnostics;

/// <summary>
/// Contabilidade dos descartes do buffer VOLÁTIL do helper (o outbox em memória do PipeClient).
/// Quando o serviço está indisponível o buffer enche e a mensagem mais antiga é descartada; antes
/// da F5 isso era silencioso, o que contraria o Princípio 7 ("queda nunca silenciosa").
///
/// Aqui só contamos: o helper não emite EVENTS_DROPPED (ele não conhece a fila). Na reconexão, ou
/// no próximo envio bem-sucedido, o relatório vai ao serviço como PipeMessage{kind:drops,
/// reason:pipe_overflow} e o serviço enfileira o EVENTS_DROPPED de verdade.
/// </summary>
public sealed class OutboxDropTracker
{
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();
    private long _count;
    private string? _oldestDroppedAtIso;

    public OutboxDropTracker(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Descartes ainda não reportados ao serviço.</summary>
    public long PendingCount
    {
        get { lock (_gate) { return _count; } }
    }

    /// <summary>Registra UM descarte por transbordo do buffer. Guarda o instante do mais antigo.</summary>
    public void RecordDrop()
    {
        lock (_gate)
        {
            _count++;
            _oldestDroppedAtIso ??= Iso.Format(_utcNow());
        }
    }

    /// <summary>
    /// Toma o relatório pendente e ZERA os contadores (o chamador é responsável por entregá-lo).
    /// Retorna null quando não houve descarte — nada é enviado nesse caso.
    /// </summary>
    public PipeMessage? TakeReport()
    {
        lock (_gate)
        {
            if (_count == 0) return null;
            var message = new PipeMessage
            {
                Kind = PipeMessage.KindDrops,
                Count = _count,
                OldestDroppedAt = _oldestDroppedAtIso,
                Reason = DropReasons.PipeOverflow
            };
            _count = 0;
            _oldestDroppedAtIso = null;
            return message;
        }
    }

    /// <summary>
    /// Devolve um relatório que NÃO conseguiu ser entregue (conexão caiu no meio): a contagem
    /// volta a acumular e o instante mais antigo é preservado — descarte contado não se perde.
    /// </summary>
    public void Restore(PipeMessage report)
    {
        lock (_gate)
        {
            _count += report.Count ?? 0;
            if (report.OldestDroppedAt is not null) _oldestDroppedAtIso = report.OldestDroppedAt;
        }
    }
}

namespace M351.Agent.Core.Collectors;

public enum IdleTransitionKind { Start, End }

public sealed class IdleTransition
{
    public IdleTransitionKind Kind { get; init; }

    /// <summary>IDLE_START: instante real do último input (payload last_input_at — N5).</summary>
    public DateTimeOffset LastInputAt { get; init; }

    /// <summary>IDLE_END: duração total do ciclo ocioso.</summary>
    public long IdleDurationMs { get; init; }
}

/// <summary>
/// Máquina de estados de ociosidade (Seção 6.2): GetLastInputInfo a cada 5 s comparado ao limiar
/// da config (default 300 s — N4). IDLE_START carrega last_input_at RETROATIVO; o agente não
/// calcula intervalos (Princípio 5) — só o fato da ociosidade.
/// </summary>
public sealed class IdleTracker
{
    private readonly Func<DateTimeOffset> _utcNow;
    private int _thresholdSec;
    private DateTimeOffset _idleStartedAt; // == last_input_at do ciclo corrente

    public bool IsIdle { get; private set; }
    public long LastIdleMs { get; private set; }

    public IdleTracker(int thresholdSec = 300, Func<DateTimeOffset>? utcNow = null)
    {
        _thresholdSec = thresholdSec;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public void UpdateThreshold(int thresholdSec) => _thresholdSec = thresholdSec;

    public IdleTransition? Sample(long idleMs)
    {
        LastIdleMs = idleMs;
        var now = _utcNow();
        var thresholdMs = _thresholdSec * 1000L;

        if (!IsIdle && idleMs >= thresholdMs)
        {
            IsIdle = true;
            _idleStartedAt = now.AddMilliseconds(-idleMs); // retroativo: instante real do último input
            return new IdleTransition { Kind = IdleTransitionKind.Start, LastInputAt = _idleStartedAt };
        }

        if (IsIdle && idleMs < thresholdMs)
        {
            IsIdle = false;
            var resumedInputAt = now.AddMilliseconds(-idleMs);
            var duration = (long)(resumedInputAt - _idleStartedAt).TotalMilliseconds;
            return new IdleTransition { Kind = IdleTransitionKind.End, IdleDurationMs = Math.Max(duration, 0) };
        }

        return null;
    }

    /// <summary>Reset ao bloquear a sessão (lock vence idle — o pipeline fecha o idle no LOCK).</summary>
    public void ResetOnLock() => IsIdle = false;
}

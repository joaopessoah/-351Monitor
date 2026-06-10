using M351.Agent.Core.Contracts;

namespace M351.Agent.Core.Time;

/// <summary>
/// Detecção de mudança de relógio (Seção 6.2): comparação contínua wall-clock vs monotônico;
/// desvio > 30 s entre os dois → TIME_CHANGED{old_utc, new_utc, delta_ms, new_tz_offset_min}.
/// </summary>
public sealed class TimeChangeDetector
{
    private readonly long _thresholdMs;
    private long? _lastMono;
    private DateTimeOffset _lastWall;

    public TimeChangeDetector(long thresholdMs = 30_000)
    {
        _thresholdMs = thresholdMs;
    }

    public TimeChangedData? Sample(DateTimeOffset wallNow, long monoNow)
    {
        if (_lastMono is null)
        {
            _lastMono = monoNow;
            _lastWall = wallNow;
            return null;
        }

        var deltaMono = monoNow - _lastMono.Value;
        var deltaWall = (long)(wallNow - _lastWall).TotalMilliseconds;
        var drift = deltaWall - deltaMono;

        var expectedWall = _lastWall.AddMilliseconds(deltaMono);
        _lastMono = monoNow;
        _lastWall = wallNow;

        if (Math.Abs(drift) <= _thresholdMs) return null;

        return new TimeChangedData
        {
            OldUtc = Iso.Format(expectedWall),
            NewUtc = Iso.Format(wallNow),
            DeltaMs = drift,
            NewTzOffsetMin = (int)TimeZoneInfo.Local.GetUtcOffset(wallNow).TotalMinutes
        };
    }
}

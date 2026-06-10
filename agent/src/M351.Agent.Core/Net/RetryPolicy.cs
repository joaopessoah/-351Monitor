namespace M351.Agent.Core.Net;

/// <summary>
/// Retry de envio — N14: backoff exponencial 5s → 10s → 30s → 1m → 5m → 10m (teto),
/// jitter ±20%; respeita Retry-After (aplicado pelo sender).
/// </summary>
public sealed class RetryPolicy
{
    private static readonly TimeSpan[] Steps =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10)
    ];

    private readonly Random _random = new();
    private int _failures;

    public int Failures => _failures;

    public TimeSpan NextDelay()
    {
        var step = Steps[Math.Min(_failures, Steps.Length - 1)];
        _failures++;
        var jitter = 1.0 + (_random.NextDouble() * 0.4 - 0.2); // ±20%
        return TimeSpan.FromMilliseconds(step.TotalMilliseconds * jitter);
    }

    public void Reset() => _failures = 0;
}

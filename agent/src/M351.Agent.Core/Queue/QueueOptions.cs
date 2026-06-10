namespace M351.Agent.Core.Queue;

/// <summary>Caps do buffer offline — N8: 7 dias OU 50.000 eventos OU 100 MB (o que vier primeiro).</summary>
public sealed class QueueOptions
{
    public int MaxEvents { get; init; } = 50_000;
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(7);
    public long MaxBytes { get; init; } = 100L * 1024 * 1024;
    public long DeadLetterMaxBytes { get; init; } = 5L * 1024 * 1024;
}

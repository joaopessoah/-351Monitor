using M351.Agent.Core.Time;
using Xunit;

namespace M351.Agent.Tests;

public class TimeChangeDetectorTests
{
    [Fact]
    public void Relogio_normal_nao_dispara()
    {
        var detector = new TimeChangeDetector();
        var wall = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(detector.Sample(wall, 1_000));
        Assert.Null(detector.Sample(wall.AddSeconds(5), 6_000));
        Assert.Null(detector.Sample(wall.AddSeconds(10), 11_000));
    }

    [Fact]
    public void Salto_de_relogio_para_frente_dispara_TIME_CHANGED()
    {
        var detector = new TimeChangeDetector();
        var wall = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        detector.Sample(wall, 1_000);

        // 5 s monotônicos, mas o wall clock saltou 1 h para a frente
        var change = detector.Sample(wall.AddHours(1).AddSeconds(5), 6_000);

        Assert.NotNull(change);
        Assert.Equal(3_600_000, change!.DeltaMs);
        Assert.Equal("2026-06-09T12:00:05.000Z", change.OldUtc);
        Assert.Equal("2026-06-09T13:00:05.000Z", change.NewUtc);
    }

    [Fact]
    public void Salto_para_tras_dispara_com_delta_negativo()
    {
        var detector = new TimeChangeDetector();
        var wall = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        detector.Sample(wall, 1_000);

        var change = detector.Sample(wall.AddMinutes(-10).AddSeconds(5), 6_000);

        Assert.NotNull(change);
        Assert.Equal(-600_000, change!.DeltaMs);
    }

    [Fact]
    public void Desvio_de_ate_30s_nao_dispara()
    {
        var detector = new TimeChangeDetector();
        var wall = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        detector.Sample(wall, 1_000);

        Assert.Null(detector.Sample(wall.AddSeconds(5 + 29), 6_000)); // 29 s de desvio ≤ 30 s
    }
}

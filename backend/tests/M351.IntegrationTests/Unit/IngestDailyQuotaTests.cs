using M351.Api.RateLimiting;
using Microsoft.Extensions.Options;

namespace M351.IntegrationTests.Unit;

/// <summary>
/// Cota diária da ingestão (Seção 5.6): zera na virada do dia UTC, conta apenas eventos ACEITOS
/// (reserva + devolução), não vaza memória (devices do dia anterior descartados) e Retry-After
/// aponta para a virada do dia UTC.
/// </summary>
public class IngestDailyQuotaTests
{
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (IngestDailyQuota Quota, FakeClock Clock) Create(
        int dailyQuota, bool enabled = true, DateTimeOffset? start = null)
    {
        var clock = new FakeClock(start ?? new DateTimeOffset(2026, 6, 9, 18, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new RateLimitingOptions
        {
            Enabled = enabled,
            DailyEventQuotaPerDevice = dailyQuota,
        });
        return (new IngestDailyQuota(options, clock), clock);
    }

    [Fact]
    public void CotaEsgotada_Rejeita_ComRetryAfterAteAViradaDoDiaUtc()
    {
        var (quota, _) = Create(dailyQuota: 10); // relógio fixo em 18:00Z
        var device = Guid.NewGuid();

        Assert.True(quota.TryReserve(device, 10).Allowed);

        var decision = quota.TryReserve(device, 1);
        Assert.False(decision.Allowed);
        Assert.Equal(6 * 60 * 60, decision.RetryAfterSeconds); // 18:00Z → 00:00Z = 6 h
    }

    [Fact]
    public void ZeraNaViradaDoDiaUtc()
    {
        var (quota, clock) = Create(dailyQuota: 10);
        var device = Guid.NewGuid();

        Assert.True(quota.TryReserve(device, 10).Allowed);
        Assert.False(quota.TryReserve(device, 1).Allowed);

        clock.Now = clock.Now.AddHours(6).AddSeconds(1); // 00:00:01Z do dia seguinte
        Assert.True(quota.TryReserve(device, 10).Allowed); // cota inteira de novo
    }

    [Fact]
    public void LoteQueNaoCabeNoSaldo_RejeitadoInteiro_SemConsumirNada()
    {
        var (quota, _) = Create(dailyQuota: 10);
        var device = Guid.NewGuid();

        Assert.True(quota.TryReserve(device, 8).Allowed);
        Assert.False(quota.TryReserve(device, 3).Allowed); // 8 + 3 > 10
        Assert.True(quota.TryReserve(device, 2).Allowed);  // o saldo de 2 segue disponível
    }

    [Fact]
    public void ReleaseUnused_DevolveApenasONaoAceito()
    {
        var (quota, _) = Create(dailyQuota: 10);
        var device = Guid.NewGuid();

        var reservation = quota.TryReserve(device, 6);
        Assert.True(reservation.Allowed);
        reservation.ReleaseUnused(acceptedCount: 2); // 4 duplicatas/rejeitados devolvidos

        Assert.True(quota.TryReserve(device, 8).Allowed);  // 2 + 8 = 10
        Assert.False(quota.TryReserve(device, 1).Allowed); // cota cheia
    }

    [Fact]
    public void ReleaseAll_FalhaNoProcessamento_NaoConsomeCota()
    {
        var (quota, _) = Create(dailyQuota: 10);
        var device = Guid.NewGuid();

        var reservation = quota.TryReserve(device, 10);
        Assert.True(reservation.Allowed);
        reservation.ReleaseAll();

        Assert.True(quota.TryReserve(device, 10).Allowed);
    }

    [Fact]
    public void LoteVazio_KeepAlive_PassaMesmoComCotaEsgotada()
    {
        var (quota, _) = Create(dailyQuota: 10);
        var device = Guid.NewGuid();

        Assert.True(quota.TryReserve(device, 10).Allowed);
        Assert.True(quota.TryReserve(device, 0).Allowed);
    }

    [Fact]
    public void ViradaDoDia_DescartaDevicesDoDiaAnterior_SemVazarMemoria()
    {
        var (quota, clock) = Create(dailyQuota: 10);
        for (var i = 0; i < 100; i++)
        {
            Assert.True(quota.TryReserve(Guid.NewGuid(), 1).Allowed);
        }

        Assert.Equal(100, quota.TrackedDeviceCount);

        clock.Now = clock.Now.AddDays(1);
        Assert.True(quota.TryReserve(Guid.NewGuid(), 1).Allowed);
        Assert.Equal(1, quota.TrackedDeviceCount); // mapa do dia anterior descartado inteiro
    }

    [Fact]
    public void Desabilitado_SempreAceita_SemRastrearDevices()
    {
        var (quota, _) = Create(dailyQuota: 1, enabled: false);
        var device = Guid.NewGuid();

        Assert.True(quota.TryReserve(device, 1_000_000).Allowed);
        Assert.True(quota.TryReserve(device, 1_000_000).Allowed);
        Assert.Equal(0, quota.TrackedDeviceCount);
    }
}

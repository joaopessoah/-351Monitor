using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;

namespace M351.Api.RateLimiting;

/// <summary>
/// Cota diária dura da ingestão (Seção 5.6): máx. N eventos ACEITOS por device por dia UTC.
/// Excedida → 429 com Retry-After até a virada do dia UTC, e o lote excedente NÃO é persistido.
///
/// Semântica de contagem: o endpoint RESERVA pelo tamanho do lote ANTES do processamento
/// (garante que aceitos/dia nunca passa da cota, mesmo com lotes concorrentes) e DEVOLVE o
/// não-aceito depois — duplicatas e rejeitados não consomem cota. Efeito colateral documentado:
/// um lote cujo tamanho bruto não cabe no saldo restante é rejeitado inteiro (429), mesmo que
/// parte dele fosse duplicata; o agente segura a fila e reenvia após o Retry-After, sem perda.
///
/// Estado em memória — aceitável no MVP single-instance (ver RateLimitingOptions):
/// (a) zera na virada do dia UTC (o mapa do dia anterior é descartado inteiro);
/// (b) conta apenas eventos aceitos (reserva + devolução pós-processamento);
/// (c) não vaza memória: devices que pararam de enviar somem junto com o mapa do dia anterior —
///     a retenção máxima é 1 dia de devices ativos.
/// </summary>
public sealed class IngestDailyQuota(IOptions<RateLimitingOptions> options, TimeProvider clock)
{
    private sealed record DayState(DateOnly DayUtc, ConcurrentDictionary<Guid, StrongBox<long>> Counters);

    private readonly RateLimitingOptions _options = options.Value;
    private DayState _state = new(DateOnly.MinValue, new ConcurrentDictionary<Guid, StrongBox<long>>());

    /// <summary>Devices rastreados no dia UTC corrente (diagnóstico/testes).</summary>
    internal int TrackedDeviceCount => Volatile.Read(ref _state).Counters.Count;

    /// <summary>
    /// Tenta reservar <paramref name="eventCount"/> eventos da cota do device para o dia UTC
    /// corrente. Lote vazio (keep-alive) sempre passa, mesmo com a cota esgotada.
    /// </summary>
    public QuotaDecision TryReserve(Guid deviceId, int eventCount)
    {
        if (!_options.Enabled)
        {
            return QuotaDecision.Unlimited;
        }

        var now = clock.GetUtcNow();
        var state = CurrentState(DateOnly.FromDateTime(now.UtcDateTime));
        var counter = state.Counters.GetOrAdd(deviceId, static _ => new StrongBox<long>());
        long quota = _options.DailyEventQuotaPerDevice;

        while (true)
        {
            var used = Volatile.Read(ref counter.Value);
            if (used + eventCount > quota)
            {
                return QuotaDecision.Rejected(SecondsUntilNextUtcDay(now));
            }

            if (Interlocked.CompareExchange(ref counter.Value, used + eventCount, used) == used)
            {
                return QuotaDecision.Reserved(counter, eventCount);
            }
        }
    }

    /// <summary>Vira o dia UTC: troca o estado inteiro — contadores zerados e devices antigos descartados.</summary>
    private DayState CurrentState(DateOnly todayUtc)
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state.DayUtc == todayUtc)
            {
                return state;
            }

            var fresh = new DayState(todayUtc, new ConcurrentDictionary<Guid, StrongBox<long>>());
            if (Interlocked.CompareExchange(ref _state, fresh, state) == state)
            {
                return fresh;
            }
        }
    }

    private static int SecondsUntilNextUtcDay(DateTimeOffset now)
    {
        var nextMidnightUtc = new DateTimeOffset(
            DateOnly.FromDateTime(now.UtcDateTime).AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return Math.Max(1, (int)Math.Ceiling((nextMidnightUtc - now).TotalSeconds));
    }
}

/// <summary>
/// Resultado de <see cref="IngestDailyQuota.TryReserve"/>. Quando permitido, o chamador DEVE
/// devolver o não-aceito após o processamento (<see cref="ReleaseUnused"/>) ou a reserva
/// inteira em caso de falha (<see cref="ReleaseAll"/>).
/// </summary>
public sealed class QuotaDecision
{
    /// <summary>Decisão única reutilizável para rate limiting desabilitado.</summary>
    internal static readonly QuotaDecision Unlimited = new(allowed: true, retryAfterSeconds: 0, counter: null, reserved: 0);

    private readonly StrongBox<long>? _counter;
    private readonly int _reserved;

    private QuotaDecision(bool allowed, int retryAfterSeconds, StrongBox<long>? counter, int reserved)
    {
        Allowed = allowed;
        RetryAfterSeconds = retryAfterSeconds;
        _counter = counter;
        _reserved = reserved;
    }

    public bool Allowed { get; }

    /// <summary>Segundos até a virada do dia UTC (válido apenas quando rejeitado).</summary>
    public int RetryAfterSeconds { get; }

    internal static QuotaDecision Rejected(int retryAfterSeconds) =>
        new(allowed: false, retryAfterSeconds, counter: null, reserved: 0);

    internal static QuotaDecision Reserved(StrongBox<long> counter, int reserved) =>
        new(allowed: true, retryAfterSeconds: 0, counter, reserved);

    /// <summary>
    /// Devolve à cota a diferença entre o reservado e os eventos efetivamente ACEITOS
    /// (duplicatas e rejeitados não consomem cota). A devolução acontece no contador do dia da
    /// reserva: se o dia virou no meio do lote, o contador antigo já foi descartado — inócuo.
    /// </summary>
    public void ReleaseUnused(int acceptedCount)
    {
        if (_counter is null)
        {
            return;
        }

        var unused = _reserved - acceptedCount;
        if (unused > 0)
        {
            Interlocked.Add(ref _counter.Value, -unused);
        }
    }

    /// <summary>Falha no processamento: nada foi persistido, devolve a reserva inteira.</summary>
    public void ReleaseAll() => ReleaseUnused(0);
}

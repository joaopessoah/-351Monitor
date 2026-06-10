namespace M351.Api.RateLimiting;

/// <summary>
/// Limites de enrollment e ingestão (Seções 5.6/5.7), configuráveis por ambiente na seção
/// <c>RateLimiting</c> do appsettings. Defaults = números canônicos da spec.
///
/// CORTE EXPLÍCITO DO MVP (documentado): todos os limitadores são EM MEMÓRIA e por instância
/// de API — não há rate limiting distribuído. O MVP roda 1 instância de API (Seção 5.6:
/// "token bucket em memória — 1 instância de API no MVP"); com múltiplas instâncias os
/// limites efetivos seriam multiplicados por N e a cota diária deixaria de ser global.
/// </summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Liga/desliga TODOS os limites (a suíte de integração desliga por default).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seção 5.7 — POST /api/v1/agent/enroll: janela fixa de 1 min por IP.</summary>
    public int EnrollPerMinutePerIp { get; set; } = 10;

    /// <summary>Seção 5.6 — POST /api/v1/ingest/batch: taxa sustentada de lotes/min por device.</summary>
    public int IngestPerMinutePerDevice { get; set; } = 6;

    /// <summary>Seção 5.6 — capacidade do token bucket por device (burst).</summary>
    public int IngestBurstPerDevice { get; set; } = 30;

    /// <summary>Seção 5.6 — cota diária dura de eventos ACEITOS por device, dia UTC.</summary>
    public int DailyEventQuotaPerDevice { get; set; } = 100_000;
}

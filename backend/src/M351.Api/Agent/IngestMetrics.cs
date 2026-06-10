using System.Diagnostics.Metrics;

namespace M351.Api.Agent;

/// <summary>Métricas mínimas da ingestão (Seção 7.7).</summary>
public static class IngestMetrics
{
    public const string MeterName = "M351.Ingest";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> EventsTotal =
        Meter.CreateCounter<long>("ingest_events_total");

    public static readonly Counter<long> RejectedTotal =
        Meter.CreateCounter<long>("ingest_rejected_total");

    public static readonly Counter<long> DuplicatesTotal =
        Meter.CreateCounter<long>("ingest_duplicates_total");

    /// <summary>Tipo desconhecido: ignorar o evento e incrementar — JAMAIS rejeitar o lote (Seção 5.3).</summary>
    public static readonly Counter<long> UnknownTypeTotal =
        Meter.CreateCounter<long>("ingest_unknown_type_total");
}

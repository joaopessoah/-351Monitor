namespace M351.Api.Contracts;

/// <summary>
/// GET /billing/billable-devices?month= (F3.7, Seção 7.4 linha 816): relatório interno
/// mensal de devices cobráveis — insumo do billing manual. criteria descreve, em texto
/// legível, a regra aplicada (a spec não a formaliza; decisão documentada no controller).
/// </summary>
public record BillableDevicesResponse(
    string Month,
    int DeviceCount,
    string Criteria,
    IReadOnlyList<BillableDeviceResponse> Items);

/// <summary>evidence = primeira regra que casou: "events" &gt; "enrolled" &gt; "keep_alive".</summary>
public record BillableDeviceResponse(
    Guid DeviceId,
    string? DisplayName,
    string Hostname,
    string Status,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? LastSeenAt,
    string Evidence);

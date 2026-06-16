namespace M351.Api.Contracts;

// ----- /api/v1/privacy/* (F4.5, Seções 7.4/8.7/9.3 — direitos do titular / DSR) -----

/// <summary>
/// 202 dos endpoints de export DSR (subject/device/tenant). Mesma forma do export de
/// relatório (ExportCreateResponse), mas o kind aqui é dsr_subject | dsr_device |
/// tenant_full e o pacote gerado é um ZIP que expira em 72h (Seção 9.3 linha 738), não o
/// CSV de 7 dias. O download é servido pelo ExportsController (/api/v1/exports/{id}/download).
/// </summary>
public sealed record DsrExportResponse(Guid Id, string Kind, string Status, DateTimeOffset CreatedAt);

/// <summary>
/// Body do DELETE /privacy/subjects/{deviceUserId}/data e /privacy/devices/{deviceId}/data.
///
/// confirmation: dupla confirmação (Seção 9.3 linha 994) — o chamador deve repetir o
/// windows_username do titular (subject) ou o hostname do device (device). Bater exatamente
/// é o gate que impede exclusão acidental ou por id trocado.
///
/// reason: motivo OBRIGATÓRIO registrado na trilha (art. 19 LGPD / DPA): a controladora
/// precisa documentar por que apagou. Mínimo de caracteres exigido (DsrService.MinReasonLength).
/// </summary>
public sealed record DsrDeleteRequest(string? Confirmation, string? Reason);

/// <summary>
/// 200 do DELETE: recibo com as contagens do hard delete (Seção 9.3 linha 994 — "recibo com
/// contagens"). RawEventsDeleted/IntervalsDeleted = dados pessoais identificáveis apagados;
/// DeviceUsersAnonymized = linhas device_users cujo windows_username/display_name viraram
/// marcador neutro; DailyRowsKept = agregados de equipe PRESERVADOS (Seção 9.3 linha 995 — a
/// exclusão do titular NÃO apaga agregados de equipe já computados, documentado no DPA).
/// </summary>
public sealed record DsrDeleteReceipt(
    int RawEventsDeleted,
    int IntervalsDeleted,
    int DeviceUsersAnonymized,
    int DailyRowsKept,
    string Note);

/// <summary>Envelope do recibo do DELETE.</summary>
public sealed record DsrDeleteResponse(DsrDeleteReceipt Receipt);

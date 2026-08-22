using System.Text.Json;
using System.Text.Json.Serialization;

namespace M351.Api.Contracts;

// ----- Enrollment (Seção 5.7) -----

public record EnrollRequest(
    string? EnrollmentKey,
    string? Hostname,
    string? MachineFingerprint,
    string? OsVersion,
    string? AgentVersion);

public record EnrollResponse(
    Guid DeviceId,
    string DeviceToken,
    int ConfigVersion,
    AgentConfigDto Config);

// ----- Config canônica (Seção 5.5 — objeto completo, sempre os 11 campos) -----

/// <param name="TransparencyUrl">
/// Página pública da ORGANIZAÇÃO (/transparencia/{slug}) — link divulgável, sem segredo.
/// </param>
/// <param name="NoticeText">
/// F5 — texto do aviso de ciência gerenciado pelo tenant; null = o agente usa o texto padrão
/// embutido nele. Os trechos fixos que protegem a base legal ("é registro de ciência, não pedido
/// de consentimento") são concatenados PELO AGENTE e não podem ser editados pelo tenant.
/// </param>
/// <param name="NoticeVersion">
/// F5 — versão do aviso: bump re-exibe o aviso na frota e gera novo NOTICE_ACK.
/// </param>
/// <param name="DeviceTransparencyUrl">
/// Página pública DO FUNCIONÁRIO daquela máquina (/t/{token}, devices.transparency_token): a
/// mesma política da organização MAIS o bloco "Este dispositivo". Null para device sem token
/// (nunca deveria acontecer depois do backfill, mas o agente precisa saber cair no
/// transparency_url por slug). A url carrega um SEGREDO de baixo valor: nunca vai para log,
/// query string de telemetria nem para o payload que o Viewer lê no portal.
/// </param>
public record AgentConfigDto(
    int HeartbeatSec,
    int ActiveWindowPollSec,
    int IdleThresholdSec,
    string WindowTitlePolicy,
    string[] MaskedPatterns,
    string[] IgnoredProcesses,
    CollectionWindowDto CollectionWindow,
    string TransparencyUrl,
    string? NoticeText,
    int NoticeVersion,
    string? DeviceTransparencyUrl);

public record CollectionWindowDto(string Mode, int[]? Days, string? Start, string? End);

// ----- Ack do batch (Seção 5.5 — resposta EXATA) -----

public record IngestAckResponse(
    int Accepted,
    int Duplicates,
    IReadOnlyList<RejectedEventDto> Rejected,
    DateTimeOffset ServerTime,
    int ConfigVersion,
    AgentConfigDto? Config,
    IReadOnlyList<DeviceCommandDto> Commands);

public record RejectedEventDto(string EventId, string Reason);

public record DeviceCommandDto(Guid Id, string Type, JsonElement Payload);

// ----- Presença (Seção 7.4 — GET /dashboard/presence) -----

public record PresenceItemResponse(
    Guid DeviceId,
    string DeviceName,
    string Hostname,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("presence_state")] string PresenceState,
    string? WindowsUsername,
    string? ForegroundProcess,
    string? ForegroundTitle,
    DateTimeOffset? StateSince,
    DateTimeOffset? AppSince,
    DateTimeOffset LastContactAt);

public record PresenceResponse(IReadOnlyList<PresenceItemResponse> Items, DateTimeOffset ServerTime);

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

// ----- Config canônica (Seção 5.5 — objeto completo, sempre os 10 campos) -----

/// <param name="NoticeText">
/// F5 — texto do aviso de ciência gerenciado pelo tenant; null = o agente usa o texto padrão
/// embutido nele. Os trechos fixos que protegem a base legal ("é registro de ciência, não pedido
/// de consentimento") são concatenados PELO AGENTE e não podem ser editados pelo tenant.
/// </param>
/// <param name="NoticeVersion">
/// F5 — versão do aviso: bump re-exibe o aviso na frota e gera novo NOTICE_ACK.
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
    int NoticeVersion);

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

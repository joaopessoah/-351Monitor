using System.Text.Json.Serialization;

namespace M351.Agent.Core.Contracts;

/// <summary>POST /api/v1/agent/enroll — Seção 5.7 do spec.</summary>
public sealed class EnrollRequest
{
    [JsonPropertyName("enrollment_key")] public string EnrollmentKey { get; set; } = "";
    [JsonPropertyName("hostname")] public string Hostname { get; set; } = "";

    /// <summary>SHA-256 hex de (MachineGuid + serial do BIOS).</summary>
    [JsonPropertyName("machine_fingerprint")] public string MachineFingerprint { get; set; } = "";

    [JsonPropertyName("os_version")] public string OsVersion { get; set; } = "";
    [JsonPropertyName("agent_version")] public string AgentVersion { get; set; } = "";
}

public sealed class EnrollResponse
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("device_token")] public string DeviceToken { get; set; } = "";
    [JsonPropertyName("config_version")] public int ConfigVersion { get; set; }
    [JsonPropertyName("config")] public AgentConfig Config { get; set; } = new();
}

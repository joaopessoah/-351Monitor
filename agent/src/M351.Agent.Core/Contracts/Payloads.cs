using System.Text.Json.Serialization;

namespace M351.Agent.Core.Contracts;

// Payloads (`data`) específicos por tipo — Seção 5.3 do spec. Nomes EXATOS.

public sealed class AgentStartData
{
    [JsonPropertyName("agent_version")] public string AgentVersion { get; set; } = "";
    [JsonPropertyName("os_version")] public string OsVersion { get; set; } = "";
    [JsonPropertyName("os_build")] public string OsBuild { get; set; } = "";
    [JsonPropertyName("hostname")] public string Hostname { get; set; } = "";
    [JsonPropertyName("boot_id")] public string BootId { get; set; } = "";
    [JsonPropertyName("uptime_ms")] public long UptimeMs { get; set; }

    /// <summary>boot | install | update | crash_recovery | service_restart</summary>
    [JsonPropertyName("start_reason")] public string StartReason { get; set; } = "";

    [JsonPropertyName("monitors")] public int Monitors { get; set; }
    [JsonPropertyName("is_vm")] public bool IsVm { get; set; }

    /// <summary>ad | aad | workgroup</summary>
    [JsonPropertyName("join_type")] public string JoinType { get; set; } = "";
}

public sealed class AgentStopData
{
    /// <summary>shutdown | service_stop | update | uninstall</summary>
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

public sealed class SessionStartData
{
    /// <summary>console | rdp</summary>
    [JsonPropertyName("logon_type")] public string LogonType { get; set; } = "";
}

public sealed class ActiveWindowData
{
    /// <summary>ex.: "chrome.exe", sempre lowercase; "(privado)" para processo ignorado.</summary>
    [JsonPropertyName("process_name")] public string ProcessName { get; set; } = "";

    [JsonPropertyName("exe_path")] public string? ExePath { get; set; }

    /// <summary>AUMID UWP, opcional.</summary>
    [JsonPropertyName("app_id")] public string? AppId { get; set; }

    /// <summary>null se política APP_ONLY ou processo ignorado.</summary>
    [JsonPropertyName("window_title")] public string? WindowTitle { get; set; }

    [JsonPropertyName("title_masked")] public bool TitleMasked { get; set; }
}

public sealed class IdleStartData
{
    /// <summary>OBRIGATÓRIO — instante real do último input (fechamento retroativo, N5).</summary>
    [JsonPropertyName("last_input_at")] public string LastInputAt { get; set; } = "";
}

public sealed class IdleEndData
{
    [JsonPropertyName("idle_duration_ms")] public long IdleDurationMs { get; set; }
}

public sealed class HeartbeatData
{
    /// <summary>active | idle | locked | no_session</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "";

    [JsonPropertyName("foreground_process")] public string? ForegroundProcess { get; set; }
    [JsonPropertyName("idle_ms")] public long? IdleMs { get; set; }
    [JsonPropertyName("queue_depth")] public long QueueDepth { get; set; }
}

public sealed class SystemResumeData
{
    [JsonPropertyName("sleep_duration_ms")] public long SleepDurationMs { get; set; }
}

public sealed class TimeChangedData
{
    [JsonPropertyName("old_utc")] public string OldUtc { get; set; } = "";
    [JsonPropertyName("new_utc")] public string NewUtc { get; set; } = "";
    [JsonPropertyName("delta_ms")] public long DeltaMs { get; set; }
    [JsonPropertyName("new_tz_offset_min")] public int NewTzOffsetMin { get; set; }
}

public sealed class EventsDroppedData
{
    [JsonPropertyName("count")] public long Count { get; set; }
    [JsonPropertyName("oldest_dropped_at")] public string? OldestDroppedAt { get; set; }

    /// <summary>retention_cap | rate_limit</summary>
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

public sealed class AgentTamperData
{
    /// <summary>helper_killed | helper_killed_repeatedly | pipe_denied</summary>
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

public sealed class NoticeAckData
{
    [JsonPropertyName("notice_version")] public int NoticeVersion { get; set; }
    [JsonPropertyName("shown_at")] public string ShownAt { get; set; } = "";
}

public sealed class PolicyAppliedData
{
    [JsonPropertyName("config_version")] public int ConfigVersion { get; set; }
}

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

    // ---- saúde operacional do agente (F5): metadados de OPERAÇÃO, nunca dado pessoal. Todos
    // injetados pelo SERVIÇO (AgentRuntime.EnrichHeartbeat) — o helper não conhece fila nem ack.
    // A página pública de transparência já declara a coleta de "saúde do agente".

    /// <summary>Lotes na dead_letter (422 do servidor): &gt; 0 significa dado local preso.</summary>
    [JsonPropertyName("dead_letter_count")] public long DeadLetterCount { get; set; }

    /// <summary>Último reason de rejeição por evento visto num ack (null se nunca houve).</summary>
    [JsonPropertyName("last_reject_code")] public string? LastRejectCode { get; set; }

    /// <summary>Working set do processo do serviço em MB (meta N: &lt; 100 MB somados — Seção 6.8).</summary>
    [JsonPropertyName("working_set_mb")] public long WorkingSetMb { get; set; }

    /// <summary>Tamanho do arquivo queue.db em bytes (cap de 100 MB da N8).</summary>
    [JsonPropertyName("queue_db_bytes")] public long QueueDbBytes { get; set; }
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

    /// <summary>retention_cap | rate_limit | pipe_overflow (ver DropReasons)</summary>
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

/// <summary>
/// Motivos canônicos de descarte do EVENTS_DROPPED (Seção 5.3). Lista FECHADA: todo descarte do
/// agente cai em um destes, porque queda de dado nunca é silenciosa (Princípio 7).
///   - retention_cap: expurgo FIFO dos caps N8 na fila SQLite do serviço;
///   - rate_limit: coalescimento N17 do helper (flapping de janela ativa);
///   - pipe_overflow: buffer volátil do helper cheio (serviço indisponível/pipe caído) — o helper
///     descarta a mensagem mais antiga e CONTA, reportando no próximo envio bem-sucedido.
/// </summary>
public static class DropReasons
{
    public const string RetentionCap = "retention_cap";
    public const string RateLimit = "rate_limit";
    public const string PipeOverflow = "pipe_overflow";

    public static readonly IReadOnlyList<string> All = [RetentionCap, RateLimit, PipeOverflow];

    public static bool IsKnown(string? reason) => reason is not null && All.Contains(reason);
}

/// <summary>
/// AGENT_ERROR (F5): falha interna do agente sem vazar conteúdo. A `message` da exceção JAMAIS
/// entra aqui — ela pode carregar caminho de arquivo, título de janela ou nome de usuário. O que
/// viaja é o tipo da exceção, um hash da pilha (para agrupar ocorrências iguais no servidor) e
/// quantas vezes o mesmo erro ocorreu na janela de agregação.
/// </summary>
public sealed class AgentErrorData
{
    /// <summary>Nome do tipo da exceção (ex.: "System.IO.IOException").</summary>
    [JsonPropertyName("error_type")] public string ErrorType { get; set; } = "";

    /// <summary>SHA-256 da stack trace, truncado (hex) — agrupa o mesmo erro sem expor a pilha.</summary>
    [JsonPropertyName("stack_hash")] public string StackHash { get; set; } = "";

    /// <summary>Ocorrências deste error_type desde o último AGENT_ERROR emitido (inclui esta).</summary>
    [JsonPropertyName("count")] public long Count { get; set; }
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

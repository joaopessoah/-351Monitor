using System.Text.Json.Serialization;

namespace M351.Agent.Core.Contracts;

/// <summary>
/// Protocolo do named pipe \\.\pipe\monitoragent.{sessionId} — JSON delimitado por linha (Seção 6.1).
/// helper → serviço: kinds "event", "update", "drops", "diag_request".
/// serviço → helper: kinds "config", "diag_result".
/// O helper NÃO acessa fila nem token — só troca mensagens.
/// </summary>
public sealed class PipeMessage
{
    public const string KindEvent = "event";
    public const string KindUpdate = "update";
    public const string KindDrops = "drops";
    public const string KindConfig = "config";

    /// <summary>
    /// helper → serviço: o usuário pediu "Enviar diagnóstico ao suporte" no tray. Quem empacota e
    /// faz o upload é o SERVIÇO (o ZIP dos logs e o device token vivem do lado dele).
    /// </summary>
    public const string KindDiagnosticsRequest = "diag_request";

    /// <summary>serviço → helper: resultado do upload de diagnóstico (campo ok), para o balão do tray.</summary>
    public const string KindDiagnosticsResult = "diag_result";

    [JsonPropertyName("kind")] public string Kind { get; set; } = "";

    [JsonPropertyName("event")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentEvent? Event { get; set; }

    // kind=drops (coalescimento N17 reportado pelo helper; o serviço emite EVENTS_DROPPED)
    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Count { get; set; }

    [JsonPropertyName("oldest_dropped_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OldestDroppedAt { get; set; }

    /// <summary>
    /// kind=drops: motivo do descarte (DropReasons). Ausente/desconhecido = rate_limit, o único
    /// motivo que o helper reportava antes da F5 (compatibilidade com helper e serviço antigos).
    /// </summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    /// <summary>kind=diag_result: true se o pacote de diagnóstico chegou ao servidor.</summary>
    [JsonPropertyName("ok")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Ok { get; set; }

    // kind=config (serviço → helper)
    [JsonPropertyName("config")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentConfig? Config { get; set; }

    [JsonPropertyName("config_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ConfigVersion { get; set; }

    [JsonPropertyName("device_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceId { get; set; }

    /// <summary>boot_id do serviço — o helper carimba os envelopes com ele.</summary>
    [JsonPropertyName("boot_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BootId { get; set; }

    [JsonPropertyName("last_sent_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastSentAt { get; set; }

    /// <summary>
    /// Estado da conexao com o servidor (servico -> helper): "ok" | "sem_rede" | "erro_certificado" |
    /// "nao_enrolado". O tray ("Status da conexao") exibe esse estado — em especial o erro de
    /// certificado de uma inspecao MITM (Secao 6.4 l.445).
    /// </summary>
    [JsonPropertyName("connection_state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectionState { get; set; }
}

using System.Text.Json.Serialization;

namespace M351.Agent.Core.Contracts;

/// <summary>
/// Objeto `config` do ack/enroll — Seção 5.5 do spec. 13 campos, sempre todos presentes.
/// (Os dois últimos, `site_capture` e `document_capture`, entraram com a coleta de sites e
/// documentos; agente antigo ignora campo desconhecido e servidor antigo simplesmente não os
/// manda — e aí valem os defaults abaixo.)
/// </summary>
public sealed class AgentConfig
{
    [JsonPropertyName("heartbeat_sec")] public int HeartbeatSec { get; set; } = 60;          // N2
    [JsonPropertyName("active_window_poll_sec")] public int ActiveWindowPollSec { get; set; } = 5;  // N1
    [JsonPropertyName("idle_threshold_sec")] public int IdleThresholdSec { get; set; } = 300;       // N4

    /// <summary>FULL | MASKED_PATTERNS | APP_ONLY (default de fábrica: MASKED_PATTERNS).</summary>
    [JsonPropertyName("window_title_policy")] public string WindowTitlePolicy { get; set; } = TitlePolicies.MaskedPatterns;

    [JsonPropertyName("masked_patterns")] public List<string> MaskedPatterns { get; set; } = [];
    [JsonPropertyName("ignored_processes")] public List<string> IgnoredProcesses { get; set; } = [];
    [JsonPropertyName("collection_window")] public CollectionWindow CollectionWindow { get; set; } = new();
    [JsonPropertyName("transparency_url")] public string? TransparencyUrl { get; set; }

    /// <summary>
    /// F5 — corpo do aviso de ciência definido pelo TENANT; null/vazio = texto padrão embutido no
    /// agente (NoticeTextComposer.DefaultBody). Os trechos fixos que protegem a base legal são
    /// SEMPRE concatenados pelo agente, jamais editáveis pelo tenant.
    /// </summary>
    [JsonPropertyName("notice_text")] public string? NoticeText { get; set; }

    /// <summary>
    /// F5 — versão do aviso. O helper guarda localmente a versão confirmada; versão maior que a
    /// confirmada re-exibe o aviso e gera novo NOTICE_ACK.
    /// </summary>
    [JsonPropertyName("notice_version")] public int NoticeVersion { get; set; } = 1;

    /// <summary>
    /// Página pública DESTE dispositivo (/t/{token}): a mesma política da organização MAIS o bloco
    /// "Este dispositivo" (estado da instalação, jamais dado pessoal do dia). Chega pelo MESMO
    /// canal de config do transparency_url — resposta do enroll e config reentregue no ack.
    ///
    /// Null quando o servidor é anterior ao campo ou o device não tem token: nesse caso o tray cai
    /// no transparency_url por slug (ver TransparencyLink.Resolve), e nada quebra.
    ///
    /// A url carrega um segredo de baixo valor: NUNCA registrar em log nem enviar em telemetria.
    /// </summary>
    [JsonPropertyName("device_transparency_url")] public string? DeviceTransparencyUrl { get; set; }

    /// <summary>
    /// Coleta do DOMÍNIO do site em foco no navegador (nunca a URL completa). Desligado, o
    /// agente sequer lê a barra de endereço — o tempo de navegador continua contando, só sem
    /// domínio. É a chave que a controladora tem no portal para dizer "aqui não se coleta
    /// navegação", e ela some junto com o campo na página pública de transparência.
    /// </summary>
    [JsonPropertyName("site_capture")] public bool SiteCapture { get; set; } = true;

    /// <summary>
    /// Coleta do NOME do arquivo aberto (nunca o caminho, nunca o conteúdo). Desligado, o nome
    /// não é extraído nem enviado, mesmo que ele continue aparecendo no título da janela sob a
    /// política de títulos vigente.
    /// </summary>
    [JsonPropertyName("document_capture")] public bool DocumentCapture { get; set; } = true;

    /// <summary>Config de fábrica usada antes do primeiro enroll (sem config do servidor).</summary>
    public static AgentConfig FactoryDefault() => new()
    {
        HeartbeatSec = 60,
        ActiveWindowPollSec = 5,
        IdleThresholdSec = 300,
        WindowTitlePolicy = TitlePolicies.MaskedPatterns,
        MaskedPatterns =
        [
            "(?i)senha",
            @"\d{3}\.\d{3}\.\d{3}-\d{2}"
        ],
        IgnoredProcesses =
        [
            "keepass.exe", "1password.exe", "bitwarden.exe",
            "logonui.exe", "lockapp.exe", "consent.exe"
        ],
        CollectionWindow = new CollectionWindow { Mode = CollectionWindowModes.Always },
        TransparencyUrl = null,
        NoticeText = null, // aviso padrão do agente até o tenant definir o dele
        NoticeVersion = 1,
        DeviceTransparencyUrl = null, // só existe depois do enroll (o token nasce lá)
        SiteCapture = true,
        DocumentCapture = true
    };
}

public sealed class CollectionWindow
{
    /// <summary>ALWAYS | BUSINESS_HOURS</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = CollectionWindowModes.Always;

    /// <summary>Dias ISO (1=segunda … 7=domingo); null em ALWAYS.</summary>
    [JsonPropertyName("days")] public List<int>? Days { get; set; }

    /// <summary>"HH:mm" local; null em ALWAYS.</summary>
    [JsonPropertyName("start")] public string? Start { get; set; }

    [JsonPropertyName("end")] public string? End { get; set; }
}

public static class TitlePolicies
{
    public const string Full = "FULL";
    public const string MaskedPatterns = "MASKED_PATTERNS";
    public const string AppOnly = "APP_ONLY";
}

public static class CollectionWindowModes
{
    public const string Always = "ALWAYS";
    public const string BusinessHours = "BUSINESS_HOURS";
}

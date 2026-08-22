namespace M351.Domain.Entities;

/// <summary>
/// Config do agente por tenant (objeto canônico da Seção 5.5 — 8 campos), versionada.
/// Entregue EXCLUSIVAMENTE pelo ack do batch (e no enroll); bump de config_version
/// propaga no próximo ack de cada device. `transparency_url` é derivada (Portal:BaseUrl
/// + slug da org) na entrega — não é coluna.
/// </summary>
public class TenantAgentConfig : ITenantEntity
{
    public Guid TenantId { get; set; }
    public int ConfigVersion { get; set; } = 1;

    /// <summary>N2 — 60 s.</summary>
    public int HeartbeatSec { get; set; } = 60;

    /// <summary>N1 — 5 s.</summary>
    public int ActiveWindowPollSec { get; set; } = 5;

    /// <summary>N4 — default 300 s; faixa do protocolo 60–1800 s.</summary>
    public int IdleThresholdSec { get; set; } = 300;

    /// <summary>FULL | MASKED_PATTERNS | APP_ONLY (default de fábrica: MASKED_PATTERNS).</summary>
    public string WindowTitlePolicy { get; set; } = "MASKED_PATTERNS";

    public string[] MaskedPatterns { get; set; } = FactoryDefaults.MaskedPatterns;
    public string[] IgnoredProcesses { get; set; } = FactoryDefaults.IgnoredProcesses;

    /// <summary>JSON: {"mode":"ALWAYS|BUSINESS_HOURS","days":[1..5],"start":"08:00","end":"18:00"}.</summary>
    public string CollectionWindow { get; set; } = FactoryDefaults.CollectionWindowAlways;

    /// <summary>
    /// F5 — texto do aviso de ciência (NoticeForm) gerenciado pelo tenant; null = texto padrão
    /// embutido no agente. Viaja na config do ack (Seção 5.5 estendida de 8 para 10 campos,
    /// JSON desconhecido é ignorado por agentes antigos). Os trechos fixos que protegem a base
    /// legal ("registro de ciência, não consentimento") são concatenados pelo AGENTE e não
    /// fazem parte deste texto.
    /// </summary>
    public string? NoticeText { get; set; }

    /// <summary>
    /// F5 — versão do aviso de ciência: bump re-exibe o NoticeForm na frota (o MaybeShowNotice
    /// do agente re-exibe quando a versão local é menor) e gera novo NOTICE_ACK.
    /// </summary>
    public int NoticeVersion { get; set; } = 1;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Defaults de fábrica (Seções 5.5, 6.3 e 9.2).</summary>
    public static class FactoryDefaults
    {
        public static readonly string[] MaskedPatterns =
            ["(?i)senha", "(?i)\\bbanco\\b", "\\d{3}\\.\\d{3}\\.\\d{3}-\\d{2}"];

        public static readonly string[] IgnoredProcesses =
            ["keepass.exe", "1password.exe", "bitwarden.exe", "logonui.exe", "lockapp.exe", "consent.exe"];

        public const string CollectionWindowAlways = """{"mode":"ALWAYS","days":null,"start":null,"end":null}""";
    }
}

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using M351.Agent.Core.Collectors;
using M351.Agent.Core.Contracts;

namespace M351.Agent.Core.Privacy;

/// <summary>
/// Enforcement de privacidade NO AGENTE, aplicado ANTES de persistir na fila (Seção 6.3):
/// políticas FULL / MASKED_PATTERNS / APP_ONLY, rebaixamento automático para navegação
/// anônima/privada e processos ignorados ("o tempo conta, o conteúdo não").
/// </summary>
public sealed class TitleMasker
{
    public const int MaxTitleLength = 256;
    public const string PrivateProcessName = "(privado)";

    /// <summary>
    /// Marcas de navegação anônima/privada, comparadas por CONTÉM e case-insensitive (Seção 6.3).
    ///
    /// POR QUE "CONTÉM" E NÃO "TERMINA COM" (corrigido depois de medir os navegadores reais em
    /// 16/09/2026, Windows 11 pt-BR):
    ///   - Edge  → título Win32 "UOL — [InPrivate] — Microsoft Edge": a marca está NO MEIO, e
    ///     um teste por sufixo (que era o que existia aqui) NUNCA casava;
    ///   - Chrome→ título Win32 "Terra - … - Google Chrome", IDÊNTICO ao de uma janela normal. A
    ///     marca só aparece no NOME ACESSÍVEL do painel da janela: "… - Google Chrome (Modo
    ///     anônimo)". Por isso o <see cref="Apply"/> testa também os BrowserWindowNames da amostra.
    /// Enquanto esses dois testes falhavam, o título de janela anônima chegava ao servidor sob
    /// FULL/MASKED_PATTERNS — vazamento real, contra promessa explícita do produto.
    ///
    /// A comparação OrdinalIgnoreCase NÃO normaliza acentos, então cada variante acentuada entra
    /// como entrada própria (e as versões sem acento entram como rede de segurança).
    ///
    /// FALSO POSITIVO É ACEITO DE PROPÓSITO: uma página normal cujo título fale sobre "modo
    /// anônimo" é tratada como anônima e perde título e domínio. Errar para MENOS coleta é a
    /// direção certa deste teste.
    /// </summary>
    private static readonly string[] PrivateBrowsingMarkers =
    [
        "inprivate",             // Edge, título e nome acessível, qualquer idioma
        "incognito",             // Chrome en-US
        "modo anônimo",          // Chrome pt-BR (nome acessível da janela)
        "modo anónimo",          // Chrome pt-PT
        "modo anonimo",          // sem acento, rede de segurança
        "navegação anônima",     // Chrome pt-BR (versões antigas, no título)
        "navegação anónima",     // Chrome pt-PT
        "navegacao anonima",     // sem acento, rede de segurança
        "navegação privativa",   // Firefox pt-BR
        "navegação privada",     // Firefox pt-PT
        "navegacao privativa",   // sem acento, rede de segurança
        "private browsing",      // Firefox en-US
    ];

    /// <summary>Defaults de fábrica sempre aplicados (além da lista do tenant) + processos do próprio agente.</summary>
    private static readonly HashSet<string> FactoryIgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "keepass.exe", "1password.exe", "bitwarden.exe",
        "logonui.exe", "lockapp.exe", "consent.exe",
        "monitoragentservice.exe", "monitoragentsession.exe"
    };

    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new();

    public ActiveWindowData Apply(ForegroundSample sample, AgentConfig config)
    {
        var processName = sample.ProcessName.ToLowerInvariant();

        if (IsIgnoredProcess(processName, config))
        {
            // processo ignorado não tem título, não tem site e não tem documento: "o tempo
            // conta, o conteúdo não" vale para os três campos, sem exceção
            return new ActiveWindowData
            {
                ProcessName = PrivateProcessName,
                ExePath = null,
                AppId = null,
                WindowTitle = null,
                TitleMasked = false,
                SiteDomain = null,
                DocumentName = null
            };
        }

        var title = Truncate(sample.Title);
        var policy = config.WindowTitlePolicy;

        // Rebaixamento automático para APP_ONLY em navegação anônima, qualquer que seja a
        // política. Olha o título E os nomes acessíveis da janela: o Chrome atual só marca a
        // janela anônima no segundo (ver PrivateBrowsingMarkers).
        if (IsPrivateBrowsing(title, sample.BrowserWindowNames))
            policy = TitlePolicies.AppOnly;

        string? finalTitle;
        var masked = false;
        switch (policy)
        {
            case TitlePolicies.AppOnly:
                finalTitle = null;
                break;
            case TitlePolicies.Full:
                finalTitle = title;
                break;
            default: // MASKED_PATTERNS (default de fábrica)
                (finalTitle, masked) = MaskPatterns(title, config.MaskedPatterns);
                break;
        }

        return new ActiveWindowData
        {
            ProcessName = processName,
            ExePath = NormalizeExePath(sample.ExePath),
            AppId = sample.AppId,
            WindowTitle = finalTitle,
            TitleMasked = masked,
            SiteDomain = ResolveSiteDomain(sample, processName, policy, config),
            DocumentName = ResolveDocumentName(finalTitle, config)
        };
    }

    /// <summary>
    /// Domínio do site em foco, sob a MESMA política do título (Seção 6.3):
    ///  - APP_ONLY (inclusive o rebaixamento automático da navegação anônima) → null;
    ///  - captura de sites desligada pela controladora → null;
    ///  - processo que não é navegador → null (nem se a amostra trouxer url por engano);
    ///  - MASKED_PATTERNS cujo padrão casa com o domínio → null, o domínio INTEIRO é descartado.
    ///    Mascarar "bancobrasil.com.br" para "***brasil.com.br" produziria uma chave de catálogo
    ///    falsa; e um domínio que o tenant declarou sigiloso não deve virar linha de relatório.
    /// </summary>
    private static string? ResolveSiteDomain(
        ForegroundSample sample, string processName, string policy, AgentConfig config)
    {
        if (!config.SiteCapture) return null;
        if (policy == TitlePolicies.AppOnly) return null;
        if (sample.BrowserUrl is null) return null;
        if (!BrowserProcesses.IsBrowser(processName)) return null;

        var domain = SiteDomain.FromAddressBar(sample.BrowserUrl);
        if (domain is null) return null;

        if (policy == TitlePolicies.MaskedPatterns && MatchesAnyPattern(domain, config.MaskedPatterns))
            return null;

        return domain;
    }

    /// <summary>
    /// Nome do arquivo aberto, SEMPRE derivado do título JÁ MASCARADO (o parâmetro é o título
    /// final, pós-política): sem título não há documento, e o que o mascaramento apagou do
    /// título não reaparece aqui.
    /// </summary>
    private static string? ResolveDocumentName(string? finalTitle, AgentConfig config) =>
        config.DocumentCapture ? DocumentName.FromWindowTitle(finalTitle) : null;

    private static bool MatchesAnyPattern(string value, List<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var regex = GetRegex(pattern);
            if (regex is null) continue;
            try
            {
                if (regex.IsMatch(value)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                return true; // na dúvida, não coleta
            }
        }
        return false;
    }

    public static bool IsIgnoredProcess(string processName, AgentConfig config)
    {
        if (FactoryIgnoredProcesses.Contains(processName)) return true;
        foreach (var p in config.IgnoredProcesses)
        {
            if (string.Equals(p, processName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Normalização determinística do exe_path, aplicada ANTES de persistir na fila (Seção 6.3):
    /// o prefixo "C:\Users\&lt;qualquer&gt;\" vira "%USERPROFILE%\" para não vazar o nome da pasta do
    /// usuário (muitas vezes o nome civil) em apps instalados em %LOCALAPPDATA%. Verificado no
    /// backend (backend/src): nada lê exe_path — ele fica apenas no payload JSON bruto, como
    /// informação; o agrupamento de apps é por process_name via app_catalog (IntervalizationService,
    /// dashboards/relatórios agrupam por app_id/process_name), então a reescrita não afeta agregação.
    /// Deliberadamente NÃO aplica as masked_patterns/regex do tenant: path não é título.
    /// </summary>
    public static string? NormalizeExePath(string? exePath)
    {
        const string prefix = @"C:\Users\";
        if (exePath is null || !exePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return exePath;
        var afterUser = exePath.IndexOf('\\', prefix.Length);
        if (afterUser < 0) return exePath; // "C:\Users\nome" sem componente após o usuário
        return "%USERPROFILE%" + exePath[afterUser..];
    }

    /// <summary>
    /// A janela descrita por este texto (título Win32 ou nome acessível) é de navegação
    /// anônima/privada? Ver <see cref="PrivateBrowsingMarkers"/>.
    /// </summary>
    public static bool IsPrivateBrowsing(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var marker in PrivateBrowsingMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Versão da checagem que olha o título E os nomes acessíveis da janela do navegador — é a
    /// única que enxerga janela anônima do Chrome atual.
    /// </summary>
    public static bool IsPrivateBrowsing(string? title, IReadOnlyList<string>? browserWindowNames)
    {
        if (IsPrivateBrowsing(title)) return true;
        if (browserWindowNames is null) return false;
        foreach (var name in browserWindowNames)
        {
            if (IsPrivateBrowsing(name)) return true;
        }
        return false;
    }

    private static (string? Title, bool Masked) MaskPatterns(string? title, List<string> patterns)
    {
        if (string.IsNullOrEmpty(title)) return (title, false);
        var result = title;
        foreach (var pattern in patterns)
        {
            var regex = GetRegex(pattern);
            if (regex is null) continue;
            try { result = regex.Replace(result, "***"); }
            catch (RegexMatchTimeoutException) { /* título segue sem este padrão */ }
        }
        return (result, !string.Equals(result, title, StringComparison.Ordinal));
    }

    /// <summary>Regex compilada do padrão do tenant (cache); null se o padrão for inválido.</summary>
    private static Regex? GetRegex(string pattern) => RegexCache.GetOrAdd(pattern, static p =>
    {
        try
        {
            return new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return null; // padrão inválido vindo da config: ignorar com segurança
        }
    });

    private static string? Truncate(string? title)
    {
        if (title is null) return null;
        return title.Length <= MaxTitleLength ? title : title[..MaxTitleLength];
    }
}

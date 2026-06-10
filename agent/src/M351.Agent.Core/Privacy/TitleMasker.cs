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

    /// <summary>Heurística best-effort por sufixo de título, case-insensitive (Seção 6.3).</summary>
    private static readonly string[] PrivateBrowsingSuffixes =
    [
        "(navegação anônima)",   // Chrome
        "InPrivate",             // Edge
        "(navegação privativa)"  // Firefox
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
            return new ActiveWindowData
            {
                ProcessName = PrivateProcessName,
                ExePath = null,
                AppId = null,
                WindowTitle = null,
                TitleMasked = false
            };
        }

        var title = Truncate(sample.Title);
        var policy = config.WindowTitlePolicy;

        // Rebaixamento automático para APP_ONLY em navegação anônima, qualquer que seja a política.
        if (title is not null && IsPrivateBrowsing(title))
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
            ExePath = sample.ExePath,
            AppId = sample.AppId,
            WindowTitle = finalTitle,
            TitleMasked = masked
        };
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

    public static bool IsPrivateBrowsing(string title)
    {
        var trimmed = title.TrimEnd();
        foreach (var suffix in PrivateBrowsingSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static (string? Title, bool Masked) MaskPatterns(string? title, List<string> patterns)
    {
        if (string.IsNullOrEmpty(title)) return (title, false);
        var result = title;
        foreach (var pattern in patterns)
        {
            var regex = RegexCache.GetOrAdd(pattern, static p =>
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
            if (regex is null) continue;
            try { result = regex.Replace(result, "***"); }
            catch (RegexMatchTimeoutException) { /* título segue sem este padrão */ }
        }
        return (result, !string.Equals(result, title, StringComparison.Ordinal));
    }

    private static string? Truncate(string? title)
    {
        if (title is null) return null;
        return title.Length <= MaxTitleLength ? title : title[..MaxTitleLength];
    }
}

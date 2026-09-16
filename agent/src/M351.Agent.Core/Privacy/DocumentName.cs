using System.Text.RegularExpressions;

namespace M351.Agent.Core.Privacy;

/// <summary>
/// Extração do NOME DO ARQUIVO aberto a partir do título da janela (Seção 6.3). Puro, sem I/O e
/// sem tocar em disco: o Word, o Excel, o PowerPoint, o Acrobat e os visualizadores escrevem o
/// nome do documento no próprio título, e é de lá que ele sai.
///
/// REGRA INEGOCIÁVEL DE PRIVACIDADE: a entrada é o título JÁ MASCARADO pelo
/// <see cref="TitleMasker"/>, nunca o título cru. Como consequência estrutural, o nome do arquivo
/// nunca revela nada que a política de títulos do tenant já não revelasse:
///  - política APP_ONLY ou processo ignorado → não existe título → não existe nome de arquivo;
///  - MASKED_PATTERNS que atingiu o nome → o candidato sai com "***" e é DESCARTADO (melhor não
///    ter o nome do que publicar um nome pela metade que ainda assim insinua o original).
/// Não é conteúdo de arquivo: é o mesmo metadado que já aparece na barra de tarefas do Windows.
///
/// CAMINHO NUNCA VIAJA: editores que escrevem o caminho completo no título (Notepad++, alguns
/// visualizadores) trazem junto `C:\Users\&lt;nome civil&gt;\...`. Só o último segmento é mantido.
/// </summary>
public static class DocumentName
{
    /// <summary>Teto do nome que viaja no evento.</summary>
    public const int MaxLength = 160;

    /// <summary>
    /// Lista FECHADA de extensões consideradas "documento aberto" — a pergunta que o produto
    /// responde é "que arquivo a pessoa abriu", e a resposta útil ao gestor é documento de
    /// escritório, PDF, planilha e apresentação. Executável, mídia e arquivo temporário ficam
    /// fora de propósito: não são trabalho, seriam ruído no relatório e ampliariam a coleta sem
    /// ampliar a resposta.
    /// </summary>
    public static readonly string[] Extensions =
    [
        // PDF e texto
        "pdf", "txt", "rtf", "md",
        // Word / processadores de texto
        "doc", "docx", "docm", "dot", "dotx", "odt",
        // Excel / planilhas
        "xls", "xlsx", "xlsm", "xlsb", "ods", "csv",
        // PowerPoint / apresentações
        "ppt", "pptx", "pps", "ppsx", "odp",
        // demais da suíte de escritório
        "one", "vsd", "vsdx", "mpp", "pub", "dwg",
    ];

    /// <summary>
    /// Separadores que as aplicações usam entre o nome do arquivo e o nome do programa
    /// ("Contrato.docx - Word", "relatorio.pdf — Acrobat", "a.xlsx | Excel").
    /// </summary>
    private static readonly string[] Separators = [" - ", " – ", " — ", " | ", " • ", " · "];

    private static readonly Regex EndsWithExtension = new(
        $@"\.(?:{string.Join('|', Extensions)})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Sufixos de estado que o Office e afins penduram no nome: "[Modo de Compatibilidade]", "(Somente leitura)".</summary>
    private static readonly Regex TrailingBracket = new(
        @"\s*[\[(][^\[\]()]*[\])]\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Caracteres proibidos em nome de arquivo no Windows (fora barra, tratada à parte).</summary>
    private static readonly char[] InvalidNameChars = [':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// Nome do arquivo aberto, ou null quando o título não descreve um documento.
    /// O título é quebrado nos separadores e o PRIMEIRO trecho que termina em extensão conhecida
    /// vence — as aplicações escrevem o arquivo antes do nome do programa.
    /// </summary>
    public static string? FromWindowTitle(string? maskedTitle)
    {
        if (string.IsNullOrWhiteSpace(maskedTitle)) return null;

        foreach (var part in Split(maskedTitle))
        {
            var candidate = Clean(part);
            if (candidate is null) continue;

            try
            {
                if (!EndsWithExtension.IsMatch(candidate)) continue;
            }
            catch (RegexMatchTimeoutException)
            {
                return null; // título patológico: sem nome de arquivo, sem travar a coleta
            }

            // Caminho no título: só o último segmento sai da máquina.
            var lastSlash = candidate.LastIndexOfAny(['\\', '/']);
            if (lastSlash >= 0) candidate = candidate[(lastSlash + 1)..];

            candidate = candidate.Trim();
            if (candidate.Length == 0 || candidate.StartsWith('.')) continue;
            if (candidate.IndexOfAny(InvalidNameChars) >= 0) continue;

            // Mascaramento atingiu o nome: descartar (ver cabeçalho).
            if (candidate.Contains("***", StringComparison.Ordinal)) return null;

            return candidate.Length <= MaxLength ? candidate : candidate[..MaxLength];
        }

        return null;
    }

    /// <summary>Extensão em minúsculas (sem ponto) de um nome já extraído; null se não houver.</summary>
    public static string? ExtensionOf(string? documentName)
    {
        if (string.IsNullOrEmpty(documentName)) return null;
        var dot = documentName.LastIndexOf('.');
        if (dot < 0 || dot == documentName.Length - 1) return null;
        var ext = documentName[(dot + 1)..].ToLowerInvariant();
        return Array.IndexOf(Extensions, ext) >= 0 ? ext : null;
    }

    private static List<string> Split(string title)
    {
        var parts = new List<string> { title };
        foreach (var separator in Separators)
        {
            var next = new List<string>(parts.Count + 2);
            foreach (var part in parts) next.AddRange(part.Split(separator, StringSplitOptions.None));
            parts = next;
        }
        return parts;
    }

    /// <summary>
    /// Tira marcadores de estado das bordas ("•" de não salvo, "[Modo de Compatibilidade]").
    /// O asterisco só é tirado do FIM: no início ele pode ser o "***" do mascaramento, e comê-lo
    /// devolveria "do cliente.docx" para um título que o TitleMasker já tinha censurado.
    /// </summary>
    private static string? Clean(string part)
    {
        var text = part.Trim().TrimStart('•', '●', '▪').Trim();
        for (var i = 0; i < 3 && text.Length > 0; i++)
        {
            string stripped;
            try
            {
                stripped = TrailingBracket.Replace(text, string.Empty);
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
            if (stripped == text) break;
            text = stripped;
        }
        text = text.TrimEnd('*', '•').Trim();
        return text.Length == 0 ? null : text;
    }
}

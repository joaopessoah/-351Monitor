using System.Text.RegularExpressions;

namespace M351.Agent.Core.Logging;

/// <summary>
/// Defesa em profundidade contra vazamento de dado pessoal em log (DoD 11.3 l.1080:
/// nenhum log em nivel Information contem window_title nem nome de usuario).
///
/// A regra primaria e a disciplina nos call sites (nunca passar titulo/usuario para Info).
/// Este scrubber e a segunda camada: redige pares chave=valor sensiveis caso escapem,
/// aplicado a TODA mensagem de Information/Warning. Em Debug nada e redigido (Secao 6.3:
/// detalhe sensivel so em Debug, ativado por config com aviso).
/// </summary>
public static partial class LogScrubber
{
    // Chaves sensiveis do envelope (Secao 5.4): window_title, exe_path, windows_user, e o generico
    // "title". O VALOR pode conter espacos (um titulo de janela e texto livre), entao redigimos:
    //   - valor entre aspas: do " ao " de fechamento;
    //   - valor sem aspas: da chave ate o fim da mensagem (conservador — preferimos redigir demais
    //     a vazar parte de um titulo). A regra primaria continua sendo nunca passar titulo p/ Info.
    [GeneratedRegex(
        @"(?ix)\b(window_title|windows_user|exe_path|title)\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|.*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKvRegex();

    private const string Redacted = "***";

    /// <summary>Redige valores associados a chaves sensiveis na mensagem (best-effort, por linha).</summary>
    public static string Scrub(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;

        // Processa por linha para que o ".*" do valor sem aspas nao engula linhas seguintes.
        var lines = message.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var hadCr = lines[i].EndsWith('\r');
            var line = hadCr ? lines[i][..^1] : lines[i];
            line = SensitiveKvRegex().Replace(line, m => $"{m.Groups[1].Value}={Redacted}");
            lines[i] = hadCr ? line + "\r" : line;
        }
        return string.Join('\n', lines);
    }
}

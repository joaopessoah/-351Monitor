using System.IO.Compression;
using System.Text;

namespace M351.Agent.Core.Logging;

/// <summary>
/// Empacotamento dos logs no ZIP de suporte do --diag (Secao 6.5). O ZIP e o artefato que o
/// usuario envia ao TI/suporte, entao NAO pode vazar dado pessoal (DoD 11.3 l.1080).
///
/// Info/Warn ja sao redigidos na escrita pelo SerilogLogSink, mas o nivel Debug grava window_title /
/// windows_user / exe_path CRUS quando verbose_debug esta (ou esteve) ligado — e, com
/// rollingInterval=Day, o arquivo do dia ainda esta no diretorio logo apos desligar o verbose.
/// Por isso cada linha empacotada passa de novo pelo LogScrubber (idempotente nas linhas ja
/// redigidas), redigindo as chaves sensiveis das linhas Debug sem perder o valor diagnostico do
/// resto. NUNCA desabilitamos nada — apenas redigimos o que vaza.
/// </summary>
public static class DiagnosticsLogPackager
{
    /// <summary>
    /// Monta o ZIP de suporte completo (logs redigidos + info.txt) em <paramref name="targetZipPath"/>.
    /// Ponto ÚNICO usado pelo `--diag` do helper e pelo envio ao suporte disparado no tray, para
    /// que o pacote enviado ao servidor seja exatamente o mesmo que o usuário consegue inspecionar.
    /// </summary>
    public static void CreateSupportZip(string logsDirectory, string targetZipPath, string agentVersion)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetZipPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var zip = ZipFile.Open(targetZipPath, ZipArchiveMode.Create);
        AddScrubbedLogs(zip, logsDirectory);

        var info = zip.CreateEntry("info.txt");
        using var writer = new StreamWriter(info.Open(), new UTF8Encoding(false));
        writer.WriteLine($"versao_agente: {agentVersion}");
        writer.WriteLine($"gerado_em: {DateTime.Now:O}");
        writer.WriteLine($"maquina: {Environment.MachineName}");
    }

    /// <summary>
    /// Copia para o ZIP todos os *.log de <paramref name="logsDirectory"/> sob "logs/", redigindo
    /// cada linha pelo LogScrubber. Best-effort: arquivos em uso/inacessiveis sao pulados.
    /// </summary>
    public static void AddScrubbedLogs(ZipArchive zip, string logsDirectory)
    {
        if (!Directory.Exists(logsDirectory)) return;
        foreach (var file in Directory.EnumerateFiles(logsDirectory, "*.log"))
            AddScrubbedLog(zip, file);
    }

    /// <summary>Adiciona um unico arquivo de log ao ZIP com cada linha redigida pelo LogScrubber.</summary>
    public static void AddScrubbedLog(ZipArchive zip, string file)
    {
        try
        {
            // FileShare.ReadWrite | Delete: o servico/helper podem ter o arquivo aberto (shared:true no sink).
            using var source = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(source, Encoding.UTF8);

            var entry = zip.CreateEntry($"logs/{Path.GetFileName(file)}");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));

            string? line;
            while ((line = reader.ReadLine()) is not null)
                writer.WriteLine(LogScrubber.Scrub(line));
        }
        catch (IOException) { /* arquivo em uso/inacessivel: segue */ }
        catch (UnauthorizedAccessException) { /* sem permissao de leitura: segue */ }
    }
}

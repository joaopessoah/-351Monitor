using System.IO.Compression;
using System.Text;
using M351.Agent.Core.Logging;
using Xunit;

namespace M351.Agent.Tests;

/// <summary>
/// DoD 11.3 l.1080: nenhum log do agente contem window_title nem nome de usuario em nivel
/// Information. O scrubbing e a segunda camada (a primeira e a disciplina nos call sites) — aqui
/// validamos tanto o LogScrubber isolado quanto o conteudo real gravado pelo SerilogLogSink.
/// </summary>
public class LogScrubbingTests
{
    [Theory]
    [InlineData("janela ativa window_title=Relatorio confidencial.docx", "window_title=***")]
    [InlineData("usuario windows_user=ACME\\maria.silva logou", "windows_user=***")]
    [InlineData("processo exe_path=C:\\Users\\joao\\app.exe", "exe_path=***")]
    [InlineData("title: \"Caixa de entrada (37)\"", "title=***")]
    public void Scrub_redige_chaves_sensiveis(string input, string expectedFragment)
    {
        var scrubbed = LogScrubber.Scrub(input);
        Assert.Contains(expectedFragment, scrubbed);
        Assert.DoesNotContain("confidencial", scrubbed);
        Assert.DoesNotContain("maria.silva", scrubbed);
        Assert.DoesNotContain("Caixa de entrada", scrubbed);
        Assert.DoesNotContain("app.exe", scrubbed);
    }

    [Fact]
    public void Scrub_nao_altera_mensagem_sem_chave_sensivel()
    {
        const string msg = "Lote enviado: 12 eventos | ack: accepted=12 duplicates=0 rejected=0";
        Assert.Equal(msg, LogScrubber.Scrub(msg));
    }

    [Fact]
    public void Serilog_Information_nunca_grava_titulo_ou_usuario()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"m351-log-{Guid.NewGuid():N}");
        try
        {
            using (var sink = SerilogLogSink.CreateFile(dir, "service", verboseDebug: false))
            {
                // Mesmo que um call site descuidado vaze dado sensivel, o scrubbing o redige.
                sink.Info("ACTIVE_WINDOW_CHANGED window_title=Folha de pagamento.xlsx windows_user=ACME\\carlos");
                sink.Warn("titulo title=\"Conversa privada\" detectado");
                sink.Debug("debug com window_title=Segredo (nao deve aparecer: verboseDebug=false)");
            }

            var content = ReadAllLogs(dir);
            Assert.DoesNotContain("Folha de pagamento", content);
            Assert.DoesNotContain("carlos", content);
            Assert.DoesNotContain("Conversa privada", content);
            Assert.DoesNotContain("Segredo", content);     // Debug desligado: nada gravado
            Assert.Contains("ACTIVE_WINDOW_CHANGED", content); // o resto da mensagem permanece
            Assert.Contains("***", content);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void Serilog_Debug_so_grava_quando_verboseDebug_ligado()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"m351-log-{Guid.NewGuid():N}");
        try
        {
            using (var sink = SerilogLogSink.CreateFile(dir, "service", verboseDebug: true))
            {
                // Secao 6.3: Debug e o UNICO nivel onde detalhe sensivel pode aparecer (sem scrub).
                sink.Debug("detalhe window_title=Aba de teste");
                sink.Info("Info window_title=NaoDeveVazar");
            }

            var content = ReadAllLogs(dir);
            Assert.Contains("Aba de teste", content);       // Debug com verbose: detalhe permitido
            Assert.DoesNotContain("NaoDeveVazar", content);  // Info: sempre redigido
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void Serilog_grava_arquivo_com_data_do_dia()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"m351-log-{Guid.NewGuid():N}");
        try
        {
            using (var sink = SerilogLogSink.CreateFile(dir, "service"))
            {
                sink.Info("primeiro evento");
            }
            // rollingInterval=Day => service-yyyyMMdd.log
            var files = Directory.GetFiles(dir, "service-*.log");
            Assert.Single(files);
            Assert.Matches(@"service-\d{8}\.log$", Path.GetFileName(files[0]));
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void Diag_zip_nao_empacota_window_title_de_log_Debug_cru()
    {
        // --diag e o artefato que o usuario envia ao TI/suporte. Com verbose_debug=true o
        // SerilogLogSink grava window_title/usuario CRUS no nivel Debug; o empacotamento do ZIP
        // (DiagnosticsLogPackager) precisa redigir essas linhas. DoD 11.3 l.1080 / Secao 6.5.
        var logsDir = Path.Combine(Path.GetTempPath(), $"m351-diag-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(Path.GetTempPath(), $"m351-diag-{Guid.NewGuid():N}.zip");
        try
        {
            using (var sink = SerilogLogSink.CreateFile(logsDir, "session-1", verboseDebug: true))
            {
                // Debug cru (sem scrub na escrita): exatamente o que vaza no ZIP sem o tratamento.
                sink.Debug("foreground window_title=Contrato sigiloso.docx windows_user=ACME\\paulo.souza");
                sink.Info("evento normal de Information sem dado sensivel");
            }

            // Sanidade: o arquivo de log no disco realmente contem o dado cru (senao o teste e vazio).
            var rawOnDisk = ReadAllLogs(logsDir);
            Assert.Contains("Contrato sigiloso", rawOnDisk);
            Assert.Contains("paulo.souza", rawOnDisk);

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                DiagnosticsLogPackager.AddScrubbedLogs(zip, logsDir);

            var packaged = ReadZipText(zipPath);
            Assert.DoesNotContain("Contrato sigiloso", packaged); // titulo NAO vaza no ZIP
            Assert.DoesNotContain("paulo.souza", packaged);        // usuario NAO vaza no ZIP
            // Valor sem aspas: o scrubber redige da chave ate o fim da linha (conservador), engolindo
            // tanto o titulo quanto o windows_user que vem depois — ambos saem como "***".
            Assert.Contains("window_title=***", packaged);          // chave sensivel redigida
            Assert.Contains("evento normal de Information", packaged); // o resto permanece (utilidade diag)
        }
        finally
        {
            try { File.Delete(zipPath); } catch (IOException) { /* best-effort */ }
            TryDeleteDir(logsDir);
        }
    }

    private static string ReadZipText(string zipPath)
    {
        var sb = new StringBuilder();
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            sb.AppendLine(reader.ReadToEnd());
        }
        return sb.ToString();
    }

    private static string ReadAllLogs(string dir)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var f in Directory.GetFiles(dir, "*.log"))
            sb.AppendLine(File.ReadAllText(f));
        return sb.ToString();
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }
}

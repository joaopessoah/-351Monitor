using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace M351.Agent.Core.Logging;

/// <summary>
/// Implementacao de ILogSink sobre Serilog (Secao 4 / 6.6 l.461). Sink de arquivo rolling:
/// %ProgramData%\M351\MonitorAgent\logs\service-.log (servico) e session-{sid}-.log (helper),
/// rollingInterval=Day, fileSizeLimitBytes=5MB, retainedFileCountLimit=10, rollOnFileSizeLimit=true.
///
/// Integra com a abstracao ILogSink existente para NAO reescrever os call sites. Scrubbing LGPD
/// (DoD 11.3 l.1080): Info/Warn passam por LogScrubber (titulo/usuario nunca em Information).
/// Debug e o UNICO nivel com detalhe sensivel e so e gravado quando verboseDebug=true (Secao 6.3).
/// </summary>
public sealed class SerilogLogSink : ILogSink, IDisposable
{
    private readonly Logger _logger;
    private readonly bool _verboseDebug;

    private SerilogLogSink(Logger logger, bool verboseDebug)
    {
        _logger = logger;
        _verboseDebug = verboseDebug;
    }

    /// <summary>
    /// Cria o sink de arquivo rolling no diretorio de logs. prefix = "service" ou "session-{sid}".
    /// O hifen final do template ("{prefix}-.log") faz o Serilog inserir a data: prefix-yyyyMMdd.log.
    /// verboseDebug habilita o nivel Debug (detalhe sensivel) — desligado por padrao.
    /// </summary>
    public static SerilogLogSink CreateFile(string logsDirectory, string prefix, bool verboseDebug = false)
    {
        try { Directory.CreateDirectory(logsDirectory); } catch (Exception) { /* best-effort */ }

        var template = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";
        var path = Path.Combine(logsDirectory, $"{prefix}-.log");

        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(verboseDebug ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.File(
                path,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 5L * 1024 * 1024,   // 5 MB/arquivo
                retainedFileCountLimit: 10,             // maximo 10 arquivos
                rollOnFileSizeLimit: true,
                shared: true,                           // servico e helper podem coexistir
                outputTemplate: template)
            .CreateLogger();

        return new SerilogLogSink(logger, verboseDebug);
    }

    public void Info(string message) => _logger.Information(LogScrubber.Scrub(message));

    public void Warn(string message) => _logger.Warning(LogScrubber.Scrub(message));

    public void Error(string message, Exception? ex = null) => _logger.Error(ex, LogScrubber.Scrub(message));

    /// <summary>Debug e o unico nivel onde detalhe sensivel pode aparecer; so grava se habilitado.</summary>
    public void Debug(string message)
    {
        if (_verboseDebug) _logger.Debug(message); // sem scrub: Debug pode conter titulo/usuario (Secao 6.3)
    }

    public void Dispose() => _logger.Dispose();
}

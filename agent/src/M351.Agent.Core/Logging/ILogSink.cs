using System.Globalization;

namespace M351.Agent.Core.Logging;

/// <summary>
/// Log mínimo do agente. REGRA LGPD (Seção 6.3): mensagens em Information JAMAIS contêm
/// títulos de janela nem nomes de usuário.
/// </summary>
public interface ILogSink
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

public sealed class ConsoleLogSink : ILogSink
{
    private static string Now => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public void Info(string message) => Console.WriteLine($"[{Now} INF] {message}");
    public void Warn(string message) => Console.WriteLine($"[{Now} WRN] {message}");
    public void Error(string message, Exception? ex = null) =>
        Console.WriteLine($"[{Now} ERR] {message}{(ex is null ? "" : $" — {ex.GetType().Name}: {ex.Message}")}");
}

/// <summary>Log em arquivo best-effort (modo serviço): logs\service-yyyyMMdd.log.</summary>
public sealed class FileLogSink : ILogSink
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _prefix;

    public FileLogSink(string directory, string prefix)
    {
        _directory = directory;
        _prefix = prefix;
        try { Directory.CreateDirectory(directory); } catch (Exception) { /* best-effort */ }
    }

    public void Info(string message) => Write("INF", message);
    public void Warn(string message) => Write("WRN", message);
    public void Error(string message, Exception? ex = null) =>
        Write("ERR", $"{message}{(ex is null ? "" : $" — {ex.GetType().Name}: {ex.Message}")}");

    private void Write(string level, string message)
    {
        try
        {
            var file = Path.Combine(_directory, $"{_prefix}-{DateTime.Now:yyyyMMdd}.log");
            var line = $"[{DateTime.Now:HH:mm:ss.fff} {level}] {message}{Environment.NewLine}";
            lock (_gate) { File.AppendAllText(file, line); }
        }
        catch (Exception) { /* log nunca derruba o agente */ }
    }
}

public sealed class CompositeLogSink(params ILogSink[] sinks) : ILogSink
{
    public void Info(string message) { foreach (var s in sinks) s.Info(message); }
    public void Warn(string message) { foreach (var s in sinks) s.Warn(message); }
    public void Error(string message, Exception? ex = null) { foreach (var s in sinks) s.Error(message, ex); }
}

public sealed class NullLogSink : ILogSink
{
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}

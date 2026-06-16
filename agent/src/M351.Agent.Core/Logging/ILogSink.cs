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

    /// <summary>
    /// Nivel Debug: UNICO nivel onde detalhe sensivel (titulo/usuario) pode aparecer, e somente
    /// quando habilitado por config com aviso (Secao 6.3). Default = no-op para nao reescrever os
    /// call sites existentes; os sinks de arquivo Serilog so o gravam se VerboseDebug estiver ligado.
    /// </summary>
    void Debug(string message) { }
}

public sealed class ConsoleLogSink : ILogSink
{
    private static string Now => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public void Info(string message) => Console.WriteLine($"[{Now} INF] {message}");
    public void Warn(string message) => Console.WriteLine($"[{Now} WRN] {message}");
    public void Error(string message, Exception? ex = null) =>
        Console.WriteLine($"[{Now} ERR] {message}{(ex is null ? "" : $" — {ex.GetType().Name}: {ex.Message}")}");
}

// Log em arquivo: ver SerilogLogSink (Secao 6.6 l.461) — rotacao diaria, 5 MB/arquivo, max 10.
// O antigo FileLogSink foi removido na F4.3: Serilog e a unica implementacao de log em arquivo.

public sealed class CompositeLogSink(params ILogSink[] sinks) : ILogSink
{
    public void Info(string message) { foreach (var s in sinks) s.Info(message); }
    public void Warn(string message) { foreach (var s in sinks) s.Warn(message); }
    public void Error(string message, Exception? ex = null) { foreach (var s in sinks) s.Error(message, ex); }
    public void Debug(string message) { foreach (var s in sinks) s.Debug(message); }
}

public sealed class NullLogSink : ILogSink
{
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}

using System.Security.Cryptography;
using System.Text;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Events;

namespace M351.Agent.Core.Diagnostics;

/// <summary>
/// Emissor de AGENT_ERROR (Seção 5.3, F5) com limite de taxa: no MÁXIMO 1 evento por error_type
/// por hora. Um erro em loop (ex.: disco cheio a cada ciclo de 30 s) não pode inundar a fila nem
/// a ingestão — as ocorrências suprimidas viram o campo `count` do próximo evento, então nenhuma
/// falha desaparece da contabilidade.
///
/// PRIVACIDADE: a `message` da exceção NUNCA sai daqui (pode conter caminho de arquivo, título de
/// janela, nome de usuário). Viajam apenas o tipo da exceção e um hash da pilha.
///
/// O mesmo reporter serve os dois processos: no serviço o `emit` enfileira na fila SQLite; no
/// helper o `emit` manda pelo pipe (o helper não toca a fila).
/// </summary>
public sealed class AgentErrorReporter
{
    /// <summary>Janela de supressão por error_type.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(1);

    /// <summary>Tamanho do stack_hash em caracteres hex (SHA-256 truncado).</summary>
    public const int StackHashLength = 16;

    private readonly EventFactory _factory;
    private readonly Action<AgentEvent> _emit;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();
    private readonly Dictionary<string, ErrorWindow> _windows = new(StringComparer.Ordinal);

    public AgentErrorReporter(EventFactory factory, Action<AgentEvent> emit, Func<DateTimeOffset>? utcNow = null)
    {
        _factory = factory;
        _emit = emit;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Reporta uma exceção. Retorna true se o AGENT_ERROR foi emitido agora; false se foi apenas
    /// contabilizado dentro da janela de supressão do mesmo error_type.
    /// </summary>
    public bool Report(Exception exception, int? sessionId = null)
    {
        if (exception is OperationCanceledException) return false; // parada limpa não é erro

        var errorType = exception.GetType().FullName ?? exception.GetType().Name;
        var stackHash = HashStack(exception);
        var now = _utcNow();

        long count;
        lock (_gate)
        {
            if (_windows.TryGetValue(errorType, out var window))
            {
                window.Suppressed++;
                if (now - window.LastEmittedAt < MinInterval) return false;
                count = window.Suppressed;
                window.Suppressed = 0;
                window.LastEmittedAt = now;
            }
            else
            {
                _windows[errorType] = new ErrorWindow { LastEmittedAt = now, Suppressed = 0 };
                count = 1;
            }
        }

        _emit(_factory.Create(EventTypes.AgentError, new AgentErrorData
        {
            ErrorType = errorType,
            StackHash = stackHash,
            Count = count
        }, sessionId));
        return true;
    }

    /// <summary>
    /// SHA-256 truncado da pilha (sem a message). Sem pilha (exceção construída à mão) usamos o
    /// próprio nome do tipo, para que o hash siga estável e agrupável.
    /// </summary>
    public static string HashStack(Exception exception)
    {
        var stack = exception.StackTrace;
        if (string.IsNullOrWhiteSpace(stack))
        {
            stack = exception.GetType().FullName ?? exception.GetType().Name;
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(stack));
        return Convert.ToHexString(hash).ToLowerInvariant()[..StackHashLength];
    }

    private sealed class ErrorWindow
    {
        public DateTimeOffset LastEmittedAt { get; set; }
        public long Suppressed { get; set; }
    }
}

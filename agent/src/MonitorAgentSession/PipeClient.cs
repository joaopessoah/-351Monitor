using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Logging;

namespace MonitorAgentSession;

/// <summary>
/// Lado helper do IPC \\.\pipe\monitoragent.{sessionId}: envia eventos como JSON por linha e
/// recebe config do serviço. Reconecta com retry; mensagens pendentes ficam num buffer pequeno
/// em memória (a durabilidade é da fila SQLite DO SERVIÇO — o helper não persiste nada).
/// </summary>
public sealed class PipeClient : IDisposable
{
    private const int MaxBuffered = 2_000;

    private readonly int _sessionId;
    private readonly ILogSink _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<string> _outbox = new();
    private readonly SemaphoreSlim _signal = new(0);

    public event Action<PipeMessage>? ConfigReceived;
    public bool IsConnected { get; private set; }
    public DateTimeOffset? LastSentAt { get; private set; }

    /// <summary>Estado da conexao com o servidor reportado pelo servico (wire: ok/sem_rede/...).</summary>
    public string? ConnectionState { get; private set; }

    public PipeClient(int sessionId, ILogSink log)
    {
        _sessionId = sessionId;
        _log = log;
        _ = RunAsync(_cts.Token);
    }

    public void Send(PipeMessage message)
    {
        if (_outbox.Count >= MaxBuffered)
        {
            _outbox.TryDequeue(out _); // descarta o mais antigo do buffer volátil
        }
        _outbox.Enqueue(JsonSerializer.Serialize(message, AgentJsonContext.Default.PipeMessage));
        _signal.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", $"monitoragent.{_sessionId}",
                    PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5_000, ct);
                IsConnected = true;
                _log.Info("Conectado ao serviço (named pipe).");

                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 16 * 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 16 * 1024, leaveOpen: true)
                {
                    AutoFlush = true
                };

                var readTask = ReadLoopAsync(reader, ct);
                var writeTask = WriteLoopAsync(writer, ct);
                await Task.WhenAny(readTask, writeTask);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (TimeoutException)
            {
                // serviço ainda não criou o pipe: tenta de novo
            }
            catch (Exception ex)
            {
                _log.Warn($"Pipe indisponível ({ex.GetType().Name}) — nova tentativa em 5 s.");
            }
            finally
            {
                IsConnected = false;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return;
            try
            {
                var message = JsonSerializer.Deserialize(line, AgentJsonContext.Default.PipeMessage);
                if (message?.Kind == PipeMessage.KindConfig)
                {
                    if (message.LastSentAt is not null)
                    {
                        try { LastSentAt = M351.Agent.Core.Iso.Parse(message.LastSentAt); }
                        catch (FormatException) { /* ignora */ }
                    }
                    if (message.ConnectionState is not null) ConnectionState = message.ConnectionState;
                    ConfigReceived?.Invoke(message);
                }
            }
            catch (JsonException)
            {
                _log.Warn("Mensagem ilegível recebida do serviço (descartada).");
            }
        }
    }

    private async Task WriteLoopAsync(StreamWriter writer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _signal.WaitAsync(ct);
            if (!_outbox.TryDequeue(out var line)) continue;
            try
            {
                await writer.WriteLineAsync(line.AsMemory(), ct);
            }
            catch (Exception)
            {
                // conexão caiu: devolve à frente do buffer (reconexão reenvia)
                _outbox.Enqueue(line);
                _signal.Release();
                throw;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

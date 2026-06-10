using M351.Agent.Core.Queue;

namespace M351.Agent.Tests.Support;

/// <summary>Fila SQLite em arquivo temporário, removida no Dispose.</summary>
public sealed class TempQueue : IDisposable
{
    public string DbPath { get; }
    public SqliteEventQueue Queue { get; private set; }
    private readonly QueueOptions? _options;

    public TempQueue(QueueOptions? options = null)
    {
        _options = options;
        DbPath = Path.Combine(Path.GetTempPath(), $"m351-queue-{Guid.NewGuid():N}.db");
        Queue = new SqliteEventQueue(DbPath, options);
    }

    /// <summary>Simula restart do agente: fecha e reabre a mesma fila.</summary>
    public void Reopen()
    {
        Queue.Dispose();
        Queue = new SqliteEventQueue(DbPath, _options);
    }

    public void Dispose()
    {
        Queue.Dispose();
        try
        {
            File.Delete(DbPath);
            File.Delete(DbPath + "-wal");
            File.Delete(DbPath + "-shm");
        }
        catch (IOException) { /* best-effort */ }
    }
}

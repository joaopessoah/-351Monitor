using System.Text.Json;
using M351.Agent.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace M351.Agent.Core.Queue;

/// <summary>
/// Fila durável do agente — Seção 6.4 do spec.
/// SQLite WAL com tabelas: events (o `seq` do envelope É o AUTOINCREMENT), kv e dead_letter.
/// Eventos só são apagados após ack (sent=1 no ack; deleção física periódica).
/// Caps N8 com expurgo FIFO emitindo EVENTS_DROPPED{reason:retention_cap}.
/// </summary>
public sealed class SqliteEventQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _conn;
    private readonly QueueOptions _options;

    /// <summary>Fábrica do evento EVENTS_DROPPED (count, oldest_dropped_at, reason) — ligada pelo runtime.</summary>
    public Func<long, string?, string, AgentEvent>? DropEventFactory { get; set; }

    /// <summary>Notificação de expurgo (count, reason) — para log.</summary>
    public event Action<long, string>? Dropped;

    public string DbPath { get; }

    public SqliteEventQueue(string dbPath, QueueOptions? options = null)
    {
        _options = options ?? new QueueOptions();
        DbPath = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(DbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={DbPath}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS events(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              event_id TEXT UNIQUE,
              type TEXT,
              payload TEXT,
              created_at_utc TEXT,
              sent INTEGER DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_events_sent ON events(sent, seq);
            CREATE TABLE IF NOT EXISTS kv(
              key TEXT PRIMARY KEY,
              value TEXT
            );
            CREATE TABLE IF NOT EXISTS dead_letter(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              batch_json TEXT,
              created_at_utc TEXT
            );
            """);
    }

    // ---------------------------------------------------------------- events

    /// <summary>Enfileira e atribui o seq monotônico (AUTOINCREMENT). Aplica os caps N8.</summary>
    public long Enqueue(AgentEvent ev)
    {
        lock (_gate)
        {
            var seq = InsertLocked(ev);
            ev.Seq = seq;
            EnforceCapsLocked();
            return seq;
        }
    }

    private long InsertLocked(AgentEvent ev)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO events(event_id, type, payload, created_at_utc, sent)
            VALUES ($id, $type, $payload, $created, 0);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$id", ev.EventId);
        cmd.Parameters.AddWithValue("$type", ev.Type);
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(ev, AgentJsonContext.Default.AgentEvent));
        cmd.Parameters.AddWithValue("$created", Iso.Format(DateTimeOffset.UtcNow));
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Próximos eventos não enviados, em ordem de seq (lote ≤ max, N3: ≤ 500).</summary>
    public IReadOnlyList<AgentEvent> PeekBatch(int max)
    {
        lock (_gate)
        {
            var list = new List<AgentEvent>(Math.Min(max, 512));
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT seq, payload FROM events WHERE sent = 0 ORDER BY seq LIMIT $max;";
            cmd.Parameters.AddWithValue("$max", max);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var seq = reader.GetInt64(0);
                var ev = JsonSerializer.Deserialize(reader.GetString(1), AgentJsonContext.Default.AgentEvent);
                if (ev is null) continue;
                ev.Seq = seq; // o seq canônico é a coluna AUTOINCREMENT
                list.Add(ev);
            }
            return list;
        }
    }

    /// <summary>Marca como enviados (após ack 200: accepted+duplicates+rejected = processados).</summary>
    public void MarkSent(IEnumerable<long> seqs)
    {
        var values = string.Join(",", seqs);
        if (values.Length == 0) return;
        lock (_gate)
        {
            Exec($"UPDATE events SET sent = 1 WHERE seq IN ({values});");
        }
    }

    /// <summary>Deleção física dos já ackados (a cada 10 min — Seção 6.4).</summary>
    public int PurgeSent()
    {
        lock (_gate) { return ExecLocked("DELETE FROM events WHERE sent = 1;"); }
    }

    /// <summary>Anti-flapping N16: atualiza o último evento local (apenas se ainda não enviado).</summary>
    public bool TryUpdateUnsent(AgentEvent ev)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE events SET payload = $payload WHERE event_id = $id AND sent = 0;";
            cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(ev, AgentJsonContext.Default.AgentEvent));
            cmd.Parameters.AddWithValue("$id", ev.EventId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>UNENROLL: descarta a fila local (única situação de descarte deliberado além do FIFO).</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            ExecLocked("DELETE FROM events;");
            ExecLocked("DELETE FROM dead_letter;");
            // sqlite_sequence NÃO é resetada: o seq permanece monotônico por device.
        }
    }

    public long UnsentCount => Scalar("SELECT COUNT(*) FROM events WHERE sent = 0;");
    public long TotalCount => Scalar("SELECT COUNT(*) FROM events;");

    /// <summary>Maior seq já atribuído (persiste mesmo com a fila vazia).</summary>
    public long CurrentSeq => Scalar("SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'events'), 0);");

    // ---------------------------------------------------------------- caps N8

    private void EnforceCapsLocked()
    {
        long purged = 0;
        string? oldest = null;

        // 7 dias
        var cutoff = Iso.Format(DateTimeOffset.UtcNow - _options.MaxAge);
        var oldestExpired = ScalarStringLocked(
            "SELECT MIN(created_at_utc) FROM events WHERE created_at_utc < $v;", cutoff);
        if (oldestExpired is not null)
        {
            oldest = oldestExpired;
            purged += ExecLocked($"DELETE FROM events WHERE created_at_utc < '{cutoff}';");
        }

        // 50.000 eventos
        var total = ScalarLocked("SELECT COUNT(*) FROM events;");
        if (total > _options.MaxEvents)
        {
            var excess = total - _options.MaxEvents;
            oldest ??= ScalarStringLocked("SELECT MIN(created_at_utc) FROM events;", null);
            purged += ExecLocked(
                $"DELETE FROM events WHERE seq IN (SELECT seq FROM events ORDER BY seq LIMIT {excess});");
        }

        // 100 MB (páginas em uso; WAL não conta no arquivo principal)
        for (var i = 0; i < 20 && UsedBytesLocked() > _options.MaxBytes; i++)
        {
            var chunk = Math.Max(ScalarLocked("SELECT COUNT(*) FROM events;") / 10, 100);
            oldest ??= ScalarStringLocked("SELECT MIN(created_at_utc) FROM events;", null);
            var deleted = ExecLocked(
                $"DELETE FROM events WHERE seq IN (SELECT seq FROM events ORDER BY seq LIMIT {chunk});");
            purged += deleted;
            if (deleted == 0) break;
        }

        if (purged > 0)
        {
            Dropped?.Invoke(purged, "retention_cap");
            var factory = DropEventFactory;
            if (factory is not null)
            {
                var dropEvent = factory(purged, oldest, "retention_cap");
                dropEvent.Seq = InsertLocked(dropEvent); // gap visível, nunca silencioso (Princípio 7)
            }
        }
    }

    private long UsedBytesLocked()
    {
        var pageSize = ScalarLocked("PRAGMA page_size;");
        var pageCount = ScalarLocked("PRAGMA page_count;");
        var freelist = ScalarLocked("PRAGMA freelist_count;");
        return pageSize * Math.Max(pageCount - freelist, 0);
    }

    // ---------------------------------------------------------------- dead letter

    /// <summary>422 (lote inteiro rejeitado): move o lote para dead_letter e prossegue (Seção 6.4).</summary>
    public void MoveToDeadLetter(string batchJson)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO dead_letter(batch_json, created_at_utc) VALUES ($json, $created);";
            cmd.Parameters.AddWithValue("$json", batchJson);
            cmd.Parameters.AddWithValue("$created", Iso.Format(DateTimeOffset.UtcNow));
            cmd.ExecuteNonQuery();

            // cap 5 MB
            while (ScalarLocked("SELECT COALESCE(SUM(LENGTH(batch_json)), 0) FROM dead_letter;") > _options.DeadLetterMaxBytes)
            {
                if (ExecLocked("DELETE FROM dead_letter WHERE id = (SELECT MIN(id) FROM dead_letter);") == 0) break;
            }
        }
    }

    // ---------------------------------------------------------------- kv

    public string? KvGet(string key)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM kv WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void KvSet(string key, string? value)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            if (value is null)
            {
                cmd.CommandText = "DELETE FROM kv WHERE key = $k;";
                cmd.Parameters.AddWithValue("$k", key);
            }
            else
            {
                cmd.CommandText = "INSERT INTO kv(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v;";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value);
            }
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- infra

    private void Exec(string sql)
    {
        lock (_gate) { ExecLocked(sql); }
    }

    private int ExecLocked(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        lock (_gate) { return ScalarLocked(sql); }
    }

    private long ScalarLocked(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private string? ScalarStringLocked(string sql, string? param)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        if (param is not null) cmd.Parameters.AddWithValue("$v", param);
        return cmd.ExecuteScalar() as string;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            SqliteConnection.ClearPool(_conn);
            _conn.Dispose();
        }
    }
}

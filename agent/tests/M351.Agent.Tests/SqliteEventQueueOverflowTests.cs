using M351.Agent.Core;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Queue;
using M351.Agent.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace M351.Agent.Tests;

/// <summary>
/// Teste de ESTOURO de buffer (DoD 11.3 l.1083 / N8): cada cap (7 dias OU 50.000 eventos OU 100 MB)
/// expurga FIFO e emite EVENTS_DROPPED{count, oldest_dropped_at, reason:"retention_cap"}.
/// Aqui e fila LOCAL sem pipeline — os 600 s do gap N7 nao se aplicam; ainda assim usamos datas sas.
/// </summary>
public class SqliteEventQueueOverflowTests
{
    private static void WithDropFactory(SqliteEventQueue queue, M351.Agent.Core.Events.EventFactory factory) =>
        queue.DropEventFactory = (count, oldest, reason) => factory.Create(EventTypes.EventsDropped,
            new EventsDroppedData { Count = count, OldestDroppedAt = oldest, Reason = reason });

    [Fact]
    public void Estouro_do_cap_de_eventos_expurga_FIFO_e_emite_um_EVENTS_DROPPED_correto()
    {
        using var temp = new TempQueue(new QueueOptions { MaxEvents = 100 });
        var factory = TestEvents.Factory();
        WithDropFactory(temp.Queue, factory);

        long dropNotificationCount = 0;
        string? dropReason = null;
        temp.Queue.Dropped += (count, reason) => { dropNotificationCount += count; dropReason = reason; };

        // Enche bem alem do cap (100): o ultimo Enqueue dispara a aplicacao dos caps.
        for (var i = 0; i < 130; i++) temp.Queue.Enqueue(TestEvents.Heartbeat(factory));

        // Notificacao de expurgo (para log) foi emitida com reason retention_cap.
        Assert.True(dropNotificationCount >= 30);
        Assert.Equal("retention_cap", dropReason);

        var all = temp.Queue.PeekBatch(1000);
        var heartbeats = all.Where(e => e.Type == EventTypes.Heartbeat).ToList();
        var drops = all.Where(e => e.Type == EventTypes.EventsDropped).ToList();

        // FIFO: os mais antigos sumiram (seq 1 expurgado), os mais novos permaneceram.
        Assert.True(heartbeats.Count <= 100);
        Assert.True(heartbeats[0].Seq > 1, "o evento mais antigo (seq 1) deve ter sido expurgado FIFO");
        Assert.True(heartbeats.Zip(heartbeats.Skip(1)).All(p => p.First.Seq < p.Second.Seq), "ordem por seq preservada");

        // EVENTS_DROPPED emitido (um por ciclo de expurgo), cada um com count/oldest/reason corretos.
        Assert.NotEmpty(drops);
        foreach (var drop in drops)
        {
            var data = drop.Data;
            Assert.Equal("retention_cap", data.GetProperty("reason").GetString());
            Assert.True(data.GetProperty("count").GetInt64() >= 1, "count do EVENTS_DROPPED deve ser positivo");
            Assert.True(data.TryGetProperty("oldest_dropped_at", out var oldest), "deve ter oldest_dropped_at");
            Assert.False(string.IsNullOrWhiteSpace(oldest.GetString()), "oldest_dropped_at nao pode ser vazio");
        }
        // A soma dos counts cobre todos os heartbeats expurgados (>= 30 acima do cap de 100).
        Assert.True(drops.Sum(d => d.Data.GetProperty("count").GetInt64()) >= 30);
    }

    [Fact]
    public void Sem_estouro_nao_emite_EVENTS_DROPPED()
    {
        using var temp = new TempQueue(new QueueOptions { MaxEvents = 100 });
        var factory = TestEvents.Factory();
        WithDropFactory(temp.Queue, factory);

        for (var i = 0; i < 50; i++) temp.Queue.Enqueue(TestEvents.Heartbeat(factory));

        var drops = temp.Queue.PeekBatch(1000).Count(e => e.Type == EventTypes.EventsDropped);
        Assert.Equal(0, drops);
    }

    [Fact]
    public void Estouro_do_cap_de_bytes_expurga_FIFO_e_emite_EVENTS_DROPPED()
    {
        // Cap de 64 KB com eventos grandes garante o estouro por BYTES antes do cap de 50k eventos.
        using var temp = new TempQueue(new QueueOptions
        {
            MaxEvents = 50_000,
            MaxBytes = 64 * 1024,
            MaxAge = TimeSpan.FromDays(7)
        });
        var factory = TestEvents.Factory();
        WithDropFactory(temp.Queue, factory);

        var big = new string('x', 2000); // ~2 KB de titulo por evento
        for (var i = 0; i < 200; i++)
        {
            temp.Queue.Enqueue(factory.Create(EventTypes.ActiveWindowChanged, new ActiveWindowData
            {
                ProcessName = "app.exe",
                ExePath = @"C:\app.exe",
                WindowTitle = big,
                TitleMasked = false
            }, 1, "S-1", @"ACME\u"));
        }

        var all = temp.Queue.PeekBatch(10_000);
        var drops = all.Where(e => e.Type == EventTypes.EventsDropped).ToList();
        Assert.NotEmpty(drops);
        Assert.Equal("retention_cap", drops[^1].Data.GetProperty("reason").GetString());

        // FIFO por bytes: o seq 1 sumiu.
        var kept = all.Where(e => e.Type == EventTypes.ActiveWindowChanged).ToList();
        Assert.True(kept.Count > 0 && kept[0].Seq > 1, "o mais antigo deve ter sido expurgado pelo cap de bytes");
    }

    [Fact]
    public void Estouro_do_cap_de_idade_expurga_eventos_antigos_e_emite_EVENTS_DROPPED()
    {
        // created_at_utc e gravado no Enqueue; para testar o cap de IDADE (7d) backdatamos as linhas
        // existentes via uma 2a conexao e entao enfileiramos 1 evento novo que dispara o expurgo.
        using var temp = new TempQueue(new QueueOptions { MaxAge = TimeSpan.FromDays(7), MaxEvents = 50_000 });
        var factory = TestEvents.Factory();
        WithDropFactory(temp.Queue, factory);

        for (var i = 0; i < 5; i++) temp.Queue.Enqueue(TestEvents.Heartbeat(factory));

        // Backdata os 5 eventos para 10 dias atras (alem do cap de 7d).
        var old = Iso.Format(DateTimeOffset.UtcNow.AddDays(-10));
        using (var conn = new SqliteConnection($"Data Source={temp.DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE events SET created_at_utc = $old WHERE type = $t;";
            cmd.Parameters.AddWithValue("$old", old);
            cmd.Parameters.AddWithValue("$t", EventTypes.Heartbeat);
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        // Evento novo (recente): dispara EnforceCaps -> os 5 antigos expiram.
        temp.Queue.Enqueue(TestEvents.Heartbeat(factory));

        var all = temp.Queue.PeekBatch(1000);
        var drops = all.Where(e => e.Type == EventTypes.EventsDropped).ToList();
        var heartbeats = all.Where(e => e.Type == EventTypes.Heartbeat).ToList();

        var drop = Assert.Single(drops);
        Assert.Equal("retention_cap", drop.Data.GetProperty("reason").GetString());
        Assert.Equal(5, drop.Data.GetProperty("count").GetInt64()); // os 5 antigos
        Assert.Equal(old, drop.Data.GetProperty("oldest_dropped_at").GetString());

        // So o heartbeat recente sobreviveu.
        Assert.Single(heartbeats);
    }
}

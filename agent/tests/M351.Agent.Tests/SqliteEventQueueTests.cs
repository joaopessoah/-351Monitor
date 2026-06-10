using System.Text.Json;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Queue;
using M351.Agent.Tests.Support;
using Xunit;

namespace M351.Agent.Tests;

public class SqliteEventQueueTests
{
    [Fact]
    public void Enqueue_atribui_seq_monotonico_crescente()
    {
        using var temp = new TempQueue();
        var factory = TestEvents.Factory();

        var s1 = temp.Queue.Enqueue(TestEvents.Heartbeat(factory));
        var s2 = temp.Queue.Enqueue(TestEvents.Heartbeat(factory));
        var s3 = temp.Queue.Enqueue(TestEvents.Heartbeat(factory));

        Assert.Equal(s1 + 1, s2);
        Assert.Equal(s2 + 1, s3);
    }

    [Fact]
    public void Seq_persiste_apos_restart_da_fila()
    {
        using var temp = new TempQueue();
        var factory = TestEvents.Factory();
        var before = temp.Queue.Enqueue(TestEvents.Heartbeat(factory));

        temp.Reopen(); // simula restart do agente

        var after = temp.Queue.Enqueue(TestEvents.Heartbeat(factory));
        Assert.True(after > before, "seq deve continuar monotônico após restart (AUTOINCREMENT persistido)");
    }

    [Fact]
    public void Seq_continua_monotonico_mesmo_apos_ClearAll()
    {
        using var temp = new TempQueue();
        var factory = TestEvents.Factory();
        var before = temp.Queue.Enqueue(TestEvents.Heartbeat(factory));

        temp.Queue.ClearAll(); // UNENROLL descarta a fila…

        var after = temp.Queue.Enqueue(TestEvents.Heartbeat(factory));
        Assert.True(after > before, "…mas o seq do device JAMAIS regride");
    }

    [Fact]
    public void PeekBatch_drena_em_ordem_e_MarkSent_exclui_do_proximo_lote()
    {
        using var temp = new TempQueue();
        var factory = TestEvents.Factory();
        for (var i = 0; i < 5; i++) temp.Queue.Enqueue(TestEvents.Heartbeat(factory));

        var batch = temp.Queue.PeekBatch(3);
        Assert.Equal(3, batch.Count);
        Assert.True(batch[0].Seq < batch[1].Seq && batch[1].Seq < batch[2].Seq);

        temp.Queue.MarkSent(batch.Select(e => e.Seq));
        var next = temp.Queue.PeekBatch(10);
        Assert.Equal(2, next.Count);
        Assert.DoesNotContain(next, e => batch.Any(b => b.Seq == e.Seq));

        // só some fisicamente no purge periódico (pós-ack)
        Assert.Equal(5, temp.Queue.TotalCount);
        temp.Queue.PurgeSent();
        Assert.Equal(2, temp.Queue.TotalCount);
    }

    [Fact]
    public void Cap_de_eventos_expurga_FIFO_e_emite_EVENTS_DROPPED()
    {
        using var temp = new TempQueue(new QueueOptions { MaxEvents = 10 });
        var factory = TestEvents.Factory();
        temp.Queue.DropEventFactory = (count, oldest, reason) =>
            factory.Create(EventTypes.EventsDropped,
                new EventsDroppedData { Count = count, OldestDroppedAt = oldest, Reason = reason });

        for (var i = 0; i < 15; i++) temp.Queue.Enqueue(TestEvents.Heartbeat(factory));

        var all = temp.Queue.PeekBatch(100);
        var dropEvents = all.Where(e => e.Type == EventTypes.EventsDropped).ToList();
        Assert.NotEmpty(dropEvents);

        var data = dropEvents[^1].Data;
        Assert.Equal("retention_cap", data.GetProperty("reason").GetString());
        Assert.True(data.GetProperty("count").GetInt64() >= 1);
        Assert.True(data.TryGetProperty("oldest_dropped_at", out _), "payload deve ter oldest_dropped_at");

        // FIFO: os mais antigos sumiram, os mais novos ficaram
        var heartbeats = all.Where(e => e.Type == EventTypes.Heartbeat).ToList();
        Assert.True(heartbeats.Count <= 10);
        Assert.True(heartbeats[0].Seq > 1, "o evento de seq 1 (mais antigo) deve ter sido expurgado");
    }

    [Fact]
    public void TryUpdateUnsent_atualiza_payload_e_falha_se_ja_enviado()
    {
        using var temp = new TempQueue();
        var factory = TestEvents.Factory();
        var ev = factory.Create(EventTypes.ActiveWindowChanged, new ActiveWindowData
        {
            ProcessName = "chrome.exe",
            ExePath = @"C:\chrome.exe",
            WindowTitle = "Aba 1",
            TitleMasked = false
        }, 1, "S-1", @"ACME\u");
        temp.Queue.Enqueue(ev);

        var updated = ev.CloneWithData(M351.Agent.Core.Events.EventFactory.ToElement(new ActiveWindowData
        {
            ProcessName = "chrome.exe",
            ExePath = @"C:\chrome.exe",
            WindowTitle = "Aba 2",
            TitleMasked = false
        }));
        Assert.True(temp.Queue.TryUpdateUnsent(updated));

        var stored = temp.Queue.PeekBatch(1).Single();
        Assert.Equal("Aba 2", stored.Data.GetProperty("window_title").GetString());
        Assert.Equal(ev.EventId, stored.EventId); // mesmo event_id e occurred_at (imutável)
        Assert.Equal(ev.OccurredAt, stored.OccurredAt);

        temp.Queue.MarkSent([stored.Seq]);
        Assert.False(temp.Queue.TryUpdateUnsent(updated), "evento já enviado não pode ser alterado");
    }

    [Fact]
    public void ClearAll_remove_eventos_e_dead_letter()
    {
        using var temp = new TempQueue();
        var factory = TestEvents.Factory();
        temp.Queue.Enqueue(TestEvents.Heartbeat(factory));
        temp.Queue.MoveToDeadLetter("{\"lote\":\"ruim\"}");

        temp.Queue.ClearAll();

        Assert.Equal(0, temp.Queue.TotalCount);
        Assert.Empty(temp.Queue.PeekBatch(10));
    }

    [Fact]
    public void Kv_roundtrip_e_delete()
    {
        using var temp = new TempQueue();
        temp.Queue.KvSet("device_id", "abc");
        Assert.Equal("abc", temp.Queue.KvGet("device_id"));

        temp.Queue.KvSet("device_id", "def");
        Assert.Equal("def", temp.Queue.KvGet("device_id"));

        temp.Queue.KvSet("device_id", null);
        Assert.Null(temp.Queue.KvGet("device_id"));
    }

    [Fact]
    public void Payload_persistido_preserva_envelope_canonico()
    {
        using var temp = new TempQueue();
        var now = new DateTimeOffset(2026, 6, 9, 14, 25, 1, 3, TimeSpan.Zero);
        var factory = TestEvents.Factory(now, mono: 86400123);
        var ev = factory.Create(EventTypes.Unlock, null, 1,
            "S-1-5-21-3623811015-3361044348-30300820-1013", @"ACME\maria.silva");
        temp.Queue.Enqueue(ev);

        var stored = temp.Queue.PeekBatch(1).Single();
        Assert.Equal("UNLOCK", stored.Type);
        Assert.Equal("2026-06-09T14:25:01.003Z", stored.OccurredAt);
        Assert.Equal(86400123, stored.MonoMs);
        Assert.Equal(TestEvents.BootId, stored.BootId);
        Assert.Equal(1, stored.SessionId);
        Assert.Equal(@"ACME\maria.silva", stored.WindowsUser);
        Assert.Equal(JsonValueKind.Object, stored.Data.ValueKind);
    }
}

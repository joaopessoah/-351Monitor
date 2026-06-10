using System.Text.Json;
using M351.Agent.Core;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Net;
using M351.Agent.Tests.Support;
using Xunit;

namespace M351.Agent.Tests;

/// <summary>Testes de contrato (Seção 11.1): envelope 5.2 e lote 5.4 EXATOS.</summary>
public class BatchContractTests
{
    [Fact]
    public void Lote_respeita_maximo_de_500_eventos_N3()
    {
        using var temp = new TempQueue();
        var factory = TestEvents.Factory();
        for (var i = 0; i < 620; i++) temp.Queue.Enqueue(TestEvents.Heartbeat(factory));

        var events = temp.Queue.PeekBatch(BatchSender.MaxBatchSize);
        Assert.Equal(500, events.Count);

        var batch = BatchSender.BuildBatch(events, "1.0.0", 4, DateTimeOffset.UtcNow);
        Assert.Equal(500, batch.Events.Count);
    }

    [Fact]
    public void Envelope_serializado_tem_exatamente_os_campos_canonicos_da_secao_5_2()
    {
        var now = new DateTimeOffset(2026, 6, 9, 14, 25, 1, 3, TimeSpan.Zero);
        var factory = TestEvents.Factory(now, mono: 86400123);
        var ev = factory.Create(EventTypes.Unlock, null, 1,
            "S-1-5-21-3623811015-3361044348-30300820-1013", @"ACME\maria.silva");
        ev.Seq = 48211;

        var json = JsonSerializer.Serialize(ev, AgentJsonContext.Default.AgentEvent);
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        string[] expected =
        [
            "event_id", "seq", "type", "occurred_at", "tz_offset_min",
            "mono_ms", "boot_id", "session_id", "windows_sid", "windows_user", "data"
        ];
        Assert.Equal(expected.OrderBy(x => x), keys.OrderBy(x => x));

        var root = doc.RootElement;
        Assert.Equal(48211, root.GetProperty("seq").GetInt64());
        Assert.Equal("UNLOCK", root.GetProperty("type").GetString());
        Assert.Equal("2026-06-09T14:25:01.003Z", root.GetProperty("occurred_at").GetString());
        Assert.Equal(86400123, root.GetProperty("mono_ms").GetInt64());
        Assert.Equal(1, root.GetProperty("session_id").GetInt32());
        Assert.Equal(@"ACME\maria.silva", root.GetProperty("windows_user").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("data").ValueKind);
        Assert.True(Guid.TryParse(root.GetProperty("event_id").GetString(), out var guid));
        // UUIDv7: versão nos bits altos do 7º byte
        Assert.Equal('7', guid.ToString("N")[12]);
    }

    [Fact]
    public void Evento_de_maquina_serializa_session_id_null_presente()
    {
        var factory = TestEvents.Factory();
        var ev = factory.Create(EventTypes.AgentStart, new AgentStartData
        {
            AgentVersion = "1.0.0", OsVersion = "Windows 11", OsBuild = "26200",
            Hostname = "NB-TESTE", BootId = TestEvents.BootId, UptimeMs = 12345,
            StartReason = "boot", Monitors = 2, IsVm = false, JoinType = "workgroup"
        });

        var json = JsonSerializer.Serialize(ev, AgentJsonContext.Default.AgentEvent);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("session_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("windows_sid").ValueKind);

        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("boot", data.GetProperty("start_reason").GetString());
        Assert.Equal("workgroup", data.GetProperty("join_type").GetString());
    }

    [Fact]
    public void Lote_tem_batch_id_agent_version_sent_at_config_version_events()
    {
        using var temp = new TempQueue();
        var factory = TestEvents.Factory();
        temp.Queue.Enqueue(TestEvents.Heartbeat(factory));
        var sentAt = new DateTimeOffset(2026, 6, 9, 14, 32, 7, 512, TimeSpan.Zero);

        var batch = BatchSender.BuildBatch(temp.Queue.PeekBatch(500), "1.0.3", 4, sentAt);
        var json = JsonSerializer.Serialize(batch, AgentJsonContext.Default.BatchRequest);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var keys = root.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "agent_version", "batch_id", "config_version", "events", "sent_at" }, keys);
        Assert.Equal("1.0.3", root.GetProperty("agent_version").GetString());
        Assert.Equal(4, root.GetProperty("config_version").GetInt32());
        Assert.Equal("2026-06-09T14:32:07.512Z", root.GetProperty("sent_at").GetString());
        Assert.True(Guid.TryParse(root.GetProperty("batch_id").GetString(), out _));
        Assert.Equal(1, root.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public void Os_17_tipos_canonicos_estao_definidos_sem_APPS_SNAPSHOT()
    {
        Assert.Equal(17, EventTypes.All.Count);
        Assert.DoesNotContain("APPS_SNAPSHOT", EventTypes.All); // cortado do MVP
        Assert.Contains("ACTIVE_WINDOW_CHANGED", EventTypes.All);
        Assert.Contains("POLICY_APPLIED", EventTypes.All);
    }

    [Fact]
    public void Occurred_at_e_imutavel_apos_gravado_na_fila()
    {
        using var temp = new TempQueue();
        var factory = TestEvents.Factory(new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero));
        var ev = TestEvents.Heartbeat(factory);
        temp.Queue.Enqueue(ev);

        temp.Reopen(); // restart: occurred_at relido da fila, não recalculado

        var stored = temp.Queue.PeekBatch(1).Single();
        Assert.Equal("2026-06-09T10:00:00.000Z", stored.OccurredAt);
    }
}

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
    public void Os_19_tipos_canonicos_estao_definidos_sem_APPS_SNAPSHOT()
    {
        Assert.Equal(19, EventTypes.All.Count);
        Assert.DoesNotContain("APPS_SNAPSHOT", EventTypes.All); // cortado do MVP
        Assert.Contains("ACTIVE_WINDOW_CHANGED", EventTypes.All);
        Assert.Contains("POLICY_APPLIED", EventTypes.All);
        Assert.Contains("AGENT_ERROR", EventTypes.All);   // 18º tipo (F5)
        Assert.Contains("UPDATE_FAILED", EventTypes.All); // 19º tipo (vigilância de rollout)
        Assert.Equal(EventTypes.All.Count, EventTypes.All.Distinct().Count());
    }

    [Fact]
    public void AGENT_ERROR_serializa_error_type_stack_hash_e_count_e_nada_mais()
    {
        var factory = TestEvents.Factory();
        var ev = factory.Create(EventTypes.AgentError, new AgentErrorData
        {
            ErrorType = "System.IO.IOException",
            StackHash = "0123456789abcdef",
            Count = 3
        });

        var json = JsonSerializer.Serialize(ev, AgentJsonContext.Default.AgentEvent);
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");

        var keys = data.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "count", "error_type", "stack_hash" }, keys);
        Assert.Equal("System.IO.IOException", data.GetProperty("error_type").GetString());
        Assert.Equal(3, data.GetProperty("count").GetInt64());
        // contrato de privacidade: não existe campo de mensagem no payload
        Assert.False(data.TryGetProperty("message", out _));
    }

    [Fact]
    public void HEARTBEAT_serializa_os_campos_de_saude_operacional_da_secao_5_3()
    {
        var factory = TestEvents.Factory();
        var ev = factory.Create(EventTypes.Heartbeat, new HeartbeatData
        {
            State = "active",
            ForegroundProcess = "excel.exe",
            IdleMs = 0,
            QueueDepth = 7,
            DeadLetterCount = 2,
            LastRejectCode = "timestamp_too_old",
            WorkingSetMb = 42,
            QueueDbBytes = 1_234_567
        }, 1);

        var json = JsonSerializer.Serialize(ev, AgentJsonContext.Default.AgentEvent);
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");

        var keys = data.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
        Assert.Equal(
            new[]
            {
                "dead_letter_count", "foreground_process", "idle_ms", "last_reject_code",
                "queue_db_bytes", "queue_depth", "state", "working_set_mb"
            },
            keys);
        Assert.Equal(7, data.GetProperty("queue_depth").GetInt64());
        Assert.Equal(2, data.GetProperty("dead_letter_count").GetInt64());
        Assert.Equal("timestamp_too_old", data.GetProperty("last_reject_code").GetString());
        Assert.Equal(42, data.GetProperty("working_set_mb").GetInt64());
        Assert.Equal(1_234_567, data.GetProperty("queue_db_bytes").GetInt64());
    }

    [Fact]
    public void UPDATE_FAILED_serializa_from_to_e_reason_e_nada_mais()
    {
        var factory = TestEvents.Factory();
        var ev = factory.Create(EventTypes.UpdateFailed, new UpdateFailedData
        {
            FromVersion = "1.0.0",
            ToVersion = "1.1.0",
            Reason = UpdateFailureReasons.Signature
        });

        var json = JsonSerializer.Serialize(ev, AgentJsonContext.Default.AgentEvent);
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");

        var keys = data.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "from_version", "reason", "to_version" }, keys);
        Assert.Equal("1.0.0", data.GetProperty("from_version").GetString());
        Assert.Equal("1.1.0", data.GetProperty("to_version").GetString());
        Assert.Equal("signature", data.GetProperty("reason").GetString());
        // contrato de privacidade: nada de mensagem crua da exceção nem caminho de arquivo
        Assert.False(data.TryGetProperty("message", out _));
        Assert.False(data.TryGetProperty("path", out _));
    }

    [Fact]
    public void Reasons_do_UPDATE_FAILED_sao_lista_fechada_e_categorizada()
    {
        Assert.Equal(new[] { "download", "hash", "signature", "install" }, UpdateFailureReasons.All);
        Assert.True(UpdateFailureReasons.IsKnown("hash"));
        Assert.False(UpdateFailureReasons.IsKnown("motivo_inventado"));
        Assert.False(UpdateFailureReasons.IsKnown(null));
    }

    [Fact]
    public void Reasons_do_EVENTS_DROPPED_sao_lista_fechada_com_pipe_overflow()
    {
        Assert.Equal(new[] { "retention_cap", "rate_limit", "pipe_overflow" }, DropReasons.All);
        Assert.True(DropReasons.IsKnown("pipe_overflow"));
        Assert.False(DropReasons.IsKnown("motivo_inventado"));
        Assert.False(DropReasons.IsKnown(null));
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

using System.Text.Json;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Diagnostics;
using M351.Agent.Core.Events;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Net;
using M351.Agent.Core.Security;
using M351.Agent.Core.Storage;
using M351.Agent.Tests.Support;
using MonitorAgentService;
using Xunit;

namespace M351.Agent.Tests;

/// <summary>
/// Telemetria de erro do agente (F5) — o princípio "queda nunca silenciosa" de ponta a ponta:
/// descarte do buffer volátil do helper CONTADO e reportado (pipe_overflow), saúde operacional no
/// HEARTBEAT e AGENT_ERROR com limite de taxa e SEM a mensagem crua da exceção.
/// </summary>
public class AgentTelemetryTests
{
    // ------------------------------------------------------------ pipe_overflow (item a)

    [Fact]
    public void Transbordo_do_buffer_do_helper_e_contado_e_nao_silencioso()
    {
        var tracker = new OutboxDropTracker();
        Assert.Equal(0, tracker.PendingCount);
        Assert.Null(tracker.TakeReport()); // sem descarte, nada é reportado

        tracker.RecordDrop();
        tracker.RecordDrop();
        tracker.RecordDrop();
        Assert.Equal(3, tracker.PendingCount);

        var report = tracker.TakeReport();
        Assert.NotNull(report);
        Assert.Equal(PipeMessage.KindDrops, report!.Kind);
        Assert.Equal(3, report.Count);
        Assert.Equal(DropReasons.PipeOverflow, report.Reason);
        Assert.NotNull(report.OldestDroppedAt);

        // relatório tomado zera os contadores (não reporta o mesmo descarte duas vezes)
        Assert.Equal(0, tracker.PendingCount);
        Assert.Null(tracker.TakeReport());
    }

    [Fact]
    public void Oldest_dropped_at_e_o_instante_do_PRIMEIRO_descarte_da_janela()
    {
        var now = new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero);
        var tracker = new OutboxDropTracker(() => now);

        tracker.RecordDrop();
        now = now.AddMinutes(5);
        tracker.RecordDrop();

        var report = tracker.TakeReport()!;
        Assert.Equal("2026-08-21T10:00:00.000Z", report.OldestDroppedAt);
        Assert.Equal(2, report.Count);
    }

    [Fact]
    public void Relatorio_nao_entregue_volta_para_a_contagem_sem_perder_nada()
    {
        var tracker = new OutboxDropTracker();
        tracker.RecordDrop();
        tracker.RecordDrop();
        var report = tracker.TakeReport()!;

        tracker.Restore(report); // a conexão caiu durante a escrita
        tracker.RecordDrop();

        var again = tracker.TakeReport()!;
        Assert.Equal(3, again.Count);
        Assert.Equal(report.OldestDroppedAt, again.OldestDroppedAt);
    }

    // ------------------------------------------------------------ saúde no HEARTBEAT (item b)

    [Fact]
    public void Fila_expoe_dead_letter_count_e_tamanho_do_arquivo()
    {
        using var temp = new TempQueue();
        Assert.Equal(0, temp.Queue.DeadLetterCount);

        temp.Queue.MoveToDeadLetter("""{"events":[]}""");
        temp.Queue.MoveToDeadLetter("""{"events":[1]}""");

        Assert.Equal(2, temp.Queue.DeadLetterCount);
        Assert.True(temp.Queue.DbFileBytes > 0, "queue.db deveria ter tamanho mensurável");
    }

    [Fact]
    public void AckProcessor_guarda_o_ultimo_reason_de_rejeicao_para_o_heartbeat()
    {
        using var temp = new TempQueue();
        var state = new AgentStateStore(temp.Queue, new PlaintextSecretProtector());
        var processor = new AckProcessor(temp.Queue, state, TestEvents.Factory(), new NullLogSink());

        Assert.Null(processor.LastRejectCode); // nada rejeitado ainda

        processor.Process(new AckResponse
        {
            Rejected =
            [
                new RejectedEvent { EventId = Guid.NewGuid().ToString(), Reason = "timestamp_too_old" },
                new RejectedEvent { EventId = Guid.NewGuid().ToString(), Reason = "invalid_event" }
            ]
        });
        Assert.Equal("invalid_event", processor.LastRejectCode);

        // ack sem rejeição NÃO apaga o histórico (o painel de saúde precisa ver que houve rejeição)
        processor.Process(new AckResponse());
        Assert.Equal("invalid_event", processor.LastRejectCode);
    }

    [Fact]
    public void EnrichHeartbeat_preenche_a_saude_operacional_que_so_o_servico_conhece()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"m351-hb-{Guid.NewGuid():N}");
        try
        {
            using var runtime = AgentRuntime.Create(new NullLogSink(), dataDir);
            runtime.Queue.Enqueue(TestEvents.Heartbeat(runtime.Factory));
            runtime.Queue.MoveToDeadLetter("""{"events":[]}""");

            var heartbeat = new HeartbeatData { State = "no_session" };
            runtime.EnrichHeartbeat(heartbeat);

            Assert.True(heartbeat.QueueDepth >= 1);
            Assert.Equal(1, heartbeat.DeadLetterCount);
            Assert.True(heartbeat.WorkingSetMb > 0, "working_set_mb deveria refletir o processo do serviço");
            Assert.True(heartbeat.QueueDbBytes > 0, "queue_db_bytes deveria refletir o arquivo queue.db");
            Assert.Null(heartbeat.LastRejectCode); // nenhum ack com rejeição neste teste
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch (Exception) { /* best-effort */ }
        }
    }

    // ------------------------------------------------------------ AGENT_ERROR (item c)

    [Fact]
    public void AGENT_ERROR_nao_carrega_a_mensagem_crua_da_excecao()
    {
        var emitted = new List<AgentEvent>();
        var reporter = new AgentErrorReporter(TestEvents.Factory(), emitted.Add);

        // mensagem com dado que JAMAIS pode sair da máquina
        var ex = Boom(new IOException(@"C:\Users\maria.silva\Documentos\folha-de-pagamento.xlsx em uso"));
        Assert.True(reporter.Report(ex));

        var ev = Assert.Single(emitted);
        Assert.Equal(EventTypes.AgentError, ev.Type);

        var json = JsonSerializer.Serialize(ev, AgentJsonContext.Default.AgentEvent);
        Assert.DoesNotContain("maria.silva", json);
        Assert.DoesNotContain("folha-de-pagamento", json);

        var data = ev.Data.Deserialize(AgentJsonContext.Default.AgentErrorData)!;
        Assert.Equal("System.IO.IOException", data.ErrorType);
        Assert.Equal(AgentErrorReporter.StackHashLength, data.StackHash.Length);
        Assert.Equal(1, data.Count);
    }

    [Fact]
    public void AGENT_ERROR_e_limitado_a_um_por_hora_por_error_type_e_agrega_no_count()
    {
        var now = new DateTimeOffset(2026, 8, 21, 8, 0, 0, TimeSpan.Zero);
        var emitted = new List<AgentEvent>();
        var reporter = new AgentErrorReporter(TestEvents.Factory(), emitted.Add, () => now);

        Assert.True(reporter.Report(Boom(new IOException("falha 1"))));   // 1º sai na hora
        Assert.False(reporter.Report(Boom(new IOException("falha 2"))));  // suprimido
        Assert.False(reporter.Report(Boom(new IOException("falha 3"))));  // suprimido
        Assert.Single(emitted);

        // outro error_type tem janela PRÓPRIA: sai imediatamente
        Assert.True(reporter.Report(Boom(new InvalidOperationException("outra falha"))));
        Assert.Equal(2, emitted.Count);

        now = now.AddMinutes(59);
        Assert.False(reporter.Report(Boom(new IOException("falha 4")))); // ainda dentro da hora

        now = now.AddMinutes(2); // 1 h 1 min do primeiro
        Assert.True(reporter.Report(Boom(new IOException("falha 5"))));

        var last = emitted[^1].Data.Deserialize(AgentJsonContext.Default.AgentErrorData)!;
        Assert.Equal("System.IO.IOException", last.ErrorType);
        Assert.Equal(4, last.Count); // as 3 suprimidas + esta: nenhuma falha desaparece da contagem
    }

    [Fact]
    public void Stack_hash_agrupa_o_mesmo_ponto_de_falha_e_separa_pontos_diferentes()
    {
        var a1 = Boom(new IOException("mensagem A"));
        var a2 = Boom(new IOException("mensagem B totalmente diferente"));
        var b = BoomOutroLugar(new IOException("mensagem A"));

        Assert.Equal(AgentErrorReporter.HashStack(a1), AgentErrorReporter.HashStack(a2));
        Assert.NotEqual(AgentErrorReporter.HashStack(a1), AgentErrorReporter.HashStack(b));
    }

    [Fact]
    public void Cancelamento_nao_e_erro_e_nao_gera_AGENT_ERROR()
    {
        var emitted = new List<AgentEvent>();
        var reporter = new AgentErrorReporter(TestEvents.Factory(), emitted.Add);

        Assert.False(reporter.Report(new OperationCanceledException("parada limpa")));
        Assert.Empty(emitted);
    }

    /// <summary>Lança e captura para que a exceção tenha StackTrace real (hash estável).</summary>
    private static Exception Boom(Exception exception)
    {
        try { throw exception; }
        catch (Exception ex) { return ex; }
    }

    private static Exception BoomOutroLugar(Exception exception)
    {
        try { throw exception; }
        catch (Exception ex) { return ex; }
    }
}

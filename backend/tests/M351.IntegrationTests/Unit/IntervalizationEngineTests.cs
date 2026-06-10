using M351.Domain.Intervalization;
using Xunit;

namespace M351.IntegrationTests.Unit;

/// <summary>
/// Cenários nomeados da Seção 11.2 (e regras da 7.3) no nível do motor puro.
/// As variantes fim-a-fim (ingest → worker → timeline) entram como testes de
/// integração quando o job e o endpoint existirem — aqui valida-se a semântica.
/// </summary>
public class IntervalizationEngineTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1013";
    private static readonly DateTimeOffset T0 = new(2026, 6, 9, 14, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(string hhmmss)
    {
        var p = hhmmss.Split(':').Select(int.Parse).ToArray();
        return new DateTimeOffset(2026, 6, 9, p[0], p[1], p.Length > 2 ? p[2] : 0, TimeSpan.Zero);
    }

    private static PipelineEvent Ev(long seq, string type, string at, string? sid = Sid,
        string? process = null, string? title = null, string? lastInput = null,
        string? hbState = null, string? oldestDropped = null) => new()
    {
        Seq = seq,
        EventType = type,
        OccurredAt = At(at),
        WindowsSid = sid,
        ProcessName = process,
        WindowTitle = title,
        LastInputAt = lastInput is null ? null : At(lastInput),
        HeartbeatState = hbState,
        OldestDroppedAt = oldestDropped is null ? null : At(oldestDropped)
    };

    private static IReadOnlyList<BuiltInterval> Build(params PipelineEvent[] events)
        => IntervalizationEngine.Build(events).Intervals;

    // ------------------------------------------------------------ 11.2: idle-retroativo
    [Fact]
    public void IdleRetroativo_FechaActiveEmLastInputAt_NuncaNoTimestampDoEvento()
    {
        // active desde 14:00; IDLE_START às 14:31:40 com last_input_at 14:26:40
        // (heartbeats N2 sustentam o intervalo no caminho — sem eles seria gap N7)
        var intervals = Build(
            Ev(1, PipelineEventTypes.ActiveWindowChanged, "14:00:00", process: "excel.exe", title: "Orcamento"),
            Ev(2, PipelineEventTypes.Heartbeat, "14:08:00", hbState: "active"),
            Ev(3, PipelineEventTypes.Heartbeat, "14:16:00", hbState: "active"),
            Ev(4, PipelineEventTypes.Heartbeat, "14:24:00", hbState: "active"),
            Ev(5, PipelineEventTypes.IdleStart, "14:31:40", lastInput: "14:26:40"),
            Ev(6, PipelineEventTypes.IdleEnd, "14:40:00"));

        var active = Assert.Single(intervals, i => i.State == IntervalStates.Active && i.StartedAt == At("14:00:00"));
        Assert.Equal(At("14:26:40"), active.EndedAt); // NUNCA 14:31:40 (5 min de ativo falso)

        var idle = Assert.Single(intervals, i => i.State == IntervalStates.Idle);
        Assert.Equal(At("14:26:40"), idle.StartedAt);
        Assert.Equal(At("14:40:00"), idle.EndedAt);
    }

    [Fact]
    public void IdleRetroativo_ComLastInputAnteriorAoInicio_ClampaNoInicioDoIntervalo()
    {
        // last_input_at anterior ao início do intervalo corrente: fecha no início (nunca negativo)
        var intervals = Build(
            Ev(1, PipelineEventTypes.ActiveWindowChanged, "14:10:00", process: "chrome.exe"),
            Ev(2, PipelineEventTypes.IdleStart, "14:19:00", lastInput: "14:05:00"),
            Ev(3, PipelineEventTypes.IdleEnd, "14:25:00"));

        Assert.DoesNotContain(intervals, i => i.State == IntervalStates.Active); // 14:10→14:10 = zero, descartado
        var idle = Assert.Single(intervals, i => i.State == IntervalStates.Idle);
        Assert.Equal(At("14:10:00"), idle.StartedAt);
        Assert.True(intervals.All(i => i.EndedAt > i.StartedAt));
    }

    // ------------------------------------------------------------ 11.2: lock-vence-idle
    [Fact]
    public void LockVenceIdle_IdleTerminaNoLock_ActiveAposUnlock()
    {
        var intervals = Build(
            Ev(1, PipelineEventTypes.ActiveWindowChanged, "14:00:00", process: "word.exe"),
            Ev(2, PipelineEventTypes.Heartbeat, "14:05:00", hbState: "active"),
            Ev(3, PipelineEventTypes.IdleStart, "14:10:00", lastInput: "14:05:00"),
            Ev(4, PipelineEventTypes.Lock, "14:15:00"),
            Ev(5, PipelineEventTypes.Heartbeat, "14:21:00", hbState: "locked"),
            Ev(6, PipelineEventTypes.Heartbeat, "14:27:00", hbState: "locked"),
            Ev(7, PipelineEventTypes.Unlock, "14:30:00"),
            Ev(8, PipelineEventTypes.ActiveWindowChanged, "14:30:05", process: "word.exe"));

        var idle = Assert.Single(intervals, i => i.State == IntervalStates.Idle);
        Assert.Equal(At("14:15:00"), idle.EndedAt); // idle termina no LOCK

        var locked = Assert.Single(intervals, i => i.State == IntervalStates.Locked);
        Assert.Equal(At("14:15:00"), locked.StartedAt);
        Assert.Equal(At("14:30:00"), locked.EndedAt);

        Assert.Contains(intervals, i => i.State == IntervalStates.Active && i.StartedAt == At("14:30:00"));
    }

    // ------------------------------------------------------------ 11.2: gap-no-data
    [Fact]
    public void GapNoData_FechaNoUltimoEvento_NoDataAteOProximo()
    {
        // último evento 10:00, próximo 10:20, sem desligamento limpo
        var intervals = Build(
            Ev(1, PipelineEventTypes.ActiveWindowChanged, "09:51:00", process: "chrome.exe"),
            Ev(2, PipelineEventTypes.Heartbeat, "10:00:00", hbState: "active"),
            Ev(3, PipelineEventTypes.ActiveWindowChanged, "10:20:00", process: "chrome.exe"));

        var active = intervals.First(i => i.State == IntervalStates.Active);
        Assert.Equal(At("10:00:00"), active.EndedAt); // fecha no último evento, sem grace

        var noData = Assert.Single(intervals, i => i.State == IntervalStates.NoData);
        Assert.Null(noData.WindowsSid); // lane de máquina
        Assert.Equal(At("10:00:00"), noData.StartedAt);
        Assert.Equal(At("10:20:00"), noData.EndedAt);
        Assert.False(noData.DataIncomplete);
    }

    [Fact]
    public void Heartbeat_MantemIntervaloAberto_SemGap()
    {
        var events = new List<PipelineEvent>
        {
            Ev(1, PipelineEventTypes.ActiveWindowChanged, "10:00:00", process: "code.exe")
        };
        for (var i = 0; i < 30; i++) // 30 min de heartbeats a cada 60 s
            events.Add(Ev(2 + i, PipelineEventTypes.Heartbeat, $"10:{i + 1:00}:00", hbState: "active"));

        var intervals = Build(events.ToArray());

        Assert.DoesNotContain(intervals, i => i.State == IntervalStates.NoData);
        var active = Assert.Single(intervals);
        Assert.Equal(At("10:00:00"), active.StartedAt);
        Assert.Equal(At("10:30:00"), active.EndedAt);
    }

    // ------------------------------------------------------------ 11.2: desligamento-limpo
    [Fact]
    public void DesligamentoLimpo_SuspendResume_OffClean_JamaisNoData()
    {
        // SYSTEM_SUSPEND 12:00, SYSTEM_RESUME 13:00 => off_clean 12:00→13:00 (JAMAIS no_data)
        var intervals = Build(
            Ev(1, PipelineEventTypes.ActiveWindowChanged, "11:30:00", process: "excel.exe"),
            Ev(2, PipelineEventTypes.Heartbeat, "11:38:00", hbState: "active"),
            Ev(3, PipelineEventTypes.Heartbeat, "11:46:00", hbState: "active"),
            Ev(4, PipelineEventTypes.Heartbeat, "11:54:00", hbState: "active"),
            Ev(5, PipelineEventTypes.SystemSuspend, "12:00:00", sid: null),
            Ev(6, PipelineEventTypes.SystemResume, "13:00:00", sid: null),
            Ev(7, PipelineEventTypes.ActiveWindowChanged, "13:00:10", process: "excel.exe"));

        var off = Assert.Single(intervals, i => i.State == IntervalStates.OffClean);
        Assert.Null(off.WindowsSid);
        Assert.Equal(At("12:00:00"), off.StartedAt);
        Assert.Equal(At("13:00:00"), off.EndedAt);
        Assert.DoesNotContain(intervals, i => i.State == IntervalStates.NoData);
    }

    [Fact]
    public void SessionEnd_AbreOffClean_SessionStartFecha()
    {
        var intervals = Build(
            Ev(1, PipelineEventTypes.ActiveWindowChanged, "17:00:00", process: "chrome.exe"),
            Ev(2, PipelineEventTypes.SessionEnd, "17:48:00"),
            Ev(3, PipelineEventTypes.SessionStart, "18:30:00"),
            Ev(4, PipelineEventTypes.ActiveWindowChanged, "18:30:05", process: "chrome.exe"));

        var off = Assert.Single(intervals, i => i.State == IntervalStates.OffClean);
        Assert.Equal(At("17:48:00"), off.StartedAt);
        Assert.Equal(At("18:30:00"), off.EndedAt);
    }

    // ------------------------------------------------------------ 11.2: lacuna-de-seq
    [Fact]
    public void LacunaDeSeq_MarcaDataIncompleteNosIntervalosAfetados()
    {
        // seq 100, 101, 105 => trecho entre 101 e 105 com data_incomplete = true
        var intervals = Build(
            Ev(100, PipelineEventTypes.ActiveWindowChanged, "09:00:00", process: "a.exe"),
            Ev(101, PipelineEventTypes.Heartbeat, "09:01:00", hbState: "active"),
            Ev(105, PipelineEventTypes.ActiveWindowChanged, "09:05:00", process: "b.exe"),
            Ev(106, PipelineEventTypes.Lock, "09:09:00"));

        // o active que cobre o trecho da lacuna (09:01→09:05 está dentro de 09:00→09:05)
        var afetado = intervals.First(i => i.State == IntervalStates.Active && i.StartedAt == At("09:00:00"));
        Assert.True(afetado.DataIncomplete);

        // o intervalo seguinte à lacuna não é afetado
        var posterior = intervals.First(i => i.StartedAt == At("09:05:00"));
        Assert.False(posterior.DataIncomplete);
    }

    [Fact]
    public void SeqDecrescente_EhReset_NaoLacuna()
    {
        // reinstalação do agente recria o AUTOINCREMENT: seq volta a 1 — não é perda
        var intervals = Build(
            Ev(500, PipelineEventTypes.ActiveWindowChanged, "09:00:00", process: "a.exe"),
            Ev(1, PipelineEventTypes.ActiveWindowChanged, "09:10:00", process: "b.exe"),
            Ev(2, PipelineEventTypes.Lock, "09:20:00"));

        Assert.All(intervals, i => Assert.False(i.DataIncomplete));
    }

    // ------------------------------------------------------------ EVENTS_DROPPED
    [Fact]
    public void EventsDropped_TrechoCobertoViraNoDataIncomplete()
    {
        var intervals = Build(
            Ev(1, PipelineEventTypes.ActiveWindowChanged, "10:00:00", process: "a.exe"),
            Ev(2, PipelineEventTypes.EventsDropped, "10:08:00", sid: null, oldestDropped: "10:03:00"),
            Ev(3, PipelineEventTypes.ActiveWindowChanged, "10:08:30", process: "a.exe"));

        var noData = Assert.Single(intervals, i => i.State == IntervalStates.NoData);
        Assert.True(noData.DataIncomplete);
        Assert.Equal(At("10:03:00"), noData.StartedAt);
        Assert.Equal(At("10:08:00"), noData.EndedAt);
    }

    // ------------------------------------------------------------ N20
    [Fact]
    public void N20_DescartaMenorQue1s_E_FundeAdjacentesIdenticos()
    {
        var intervals = Build(
            Ev(1, PipelineEventTypes.ActiveWindowChanged, "10:00:00", process: "chrome.exe", title: "Doc"),
            // alt-tab relâmpago: notepad por 500 ms
            new PipelineEvent
            {
                Seq = 2, EventType = PipelineEventTypes.ActiveWindowChanged,
                OccurredAt = At("10:05:00"), WindowsSid = Sid, ProcessName = "notepad.exe"
            },
            new PipelineEvent
            {
                Seq = 3, EventType = PipelineEventTypes.ActiveWindowChanged,
                OccurredAt = At("10:05:00").AddMilliseconds(500), WindowsSid = Sid,
                ProcessName = "chrome.exe", WindowTitle = "Doc"
            },
            Ev(4, PipelineEventTypes.Lock, "10:10:00"));

        Assert.DoesNotContain(intervals, i => i.ProcessName == "notepad.exe"); // < 1 s descartado
        // os dois trechos de chrome NÃO se fundem (há 500 ms de buraco entre eles) — mas
        // permanecem como dois intervalos válidos do mesmo app
        Assert.Equal(2, intervals.Count(i => i.ProcessName == "chrome.exe"));
    }

    [Fact]
    public void N20_FundeAdjacentesIdenticosDeVerdade()
    {
        // IDLE_END reabre active com o último app; o AWC seguinte do MESMO app+título
        // produz adjacentes idênticos que se fundem
        var intervals = Build(
            Ev(1, PipelineEventTypes.ActiveWindowChanged, "10:00:00", process: "code.exe", title: "main.cs"),
            Ev(2, PipelineEventTypes.Heartbeat, "10:05:00", hbState: "active"),
            Ev(3, PipelineEventTypes.IdleStart, "10:10:00", lastInput: "10:05:00"),
            Ev(4, PipelineEventTypes.IdleEnd, "10:19:00"),
            Ev(5, PipelineEventTypes.ActiveWindowChanged, "10:25:00", process: "code.exe", title: "main.cs"),
            Ev(6, PipelineEventTypes.Lock, "10:31:00"));

        // active reaberto em 10:19 com code.exe/main.cs + AWC idêntico em 10:25 = um só intervalo
        var ativos = intervals.Where(i => i.State == IntervalStates.Active).ToList();
        Assert.Equal(2, ativos.Count); // 10:00→10:05 e 10:19→10:31 (fundido)
        Assert.Contains(ativos, i => i.StartedAt == At("10:19:00") && i.EndedAt == At("10:31:00"));
    }

    // ------------------------------------------------------------ heartbeat no_session
    [Fact]
    public void HeartbeatNoSession_NaoGeraIntervaloDeUsuario()
    {
        var intervals = Build(
            Ev(1, PipelineEventTypes.AgentStart, "08:00:00", sid: null),
            Ev(2, PipelineEventTypes.Heartbeat, "08:01:00", sid: null, hbState: "no_session"),
            Ev(3, PipelineEventTypes.Heartbeat, "08:02:00", sid: null, hbState: "no_session"));

        Assert.Empty(intervals); // máquina ligada sem sessão: nenhum intervalo de usuário
    }

    [Fact]
    public void HeartbeatDeSessao_ComLaneVazia_AbreIntervaloNoEstadoReportado()
    {
        var intervals = Build(
            Ev(1, PipelineEventTypes.Heartbeat, "08:00:00", hbState: "active", process: "outlook.exe"),
            Ev(2, PipelineEventTypes.Heartbeat, "08:01:00", hbState: "active"),
            Ev(3, PipelineEventTypes.Lock, "08:10:00"));

        var active = Assert.Single(intervals, i => i.State == IntervalStates.Active);
        Assert.Equal(At("08:00:00"), active.StartedAt);
        Assert.Equal(At("08:10:00"), active.EndedAt);
    }

    // ------------------------------------------------------------ IDLE_END reabre com último app
    [Fact]
    public void IdleEnd_ReabreActiveComUltimoAppConhecido()
    {
        var intervals = Build(
            Ev(1, PipelineEventTypes.ActiveWindowChanged, "10:00:00", process: "excel.exe", title: "Plan"),
            Ev(2, PipelineEventTypes.IdleStart, "10:10:00", lastInput: "10:06:00"),
            Ev(3, PipelineEventTypes.IdleEnd, "10:15:00"),
            Ev(4, PipelineEventTypes.Lock, "10:20:00"));

        var reaberto = intervals.First(i => i.State == IntervalStates.Active && i.StartedAt == At("10:15:00"));
        Assert.Equal("excel.exe", reaberto.ProcessName);
    }

    // ------------------------------------------------------------ invariantes
    [Fact]
    public void Invariante_IntervalosDeUmaLane_NuncaSeSobrepoem()
    {
        // mistura agressiva de transições, inclusive retroativas
        var intervals = Build(
            Ev(1, PipelineEventTypes.ActiveWindowChanged, "09:00:00", process: "a.exe"),
            Ev(2, PipelineEventTypes.IdleStart, "09:20:00", lastInput: "09:02:00"),
            Ev(3, PipelineEventTypes.IdleEnd, "09:25:00"),
            Ev(4, PipelineEventTypes.ActiveWindowChanged, "09:26:00", process: "b.exe"),
            Ev(5, PipelineEventTypes.Lock, "09:30:00"),
            Ev(6, PipelineEventTypes.Unlock, "09:45:00"),
            Ev(7, PipelineEventTypes.IdleStart, "09:50:00", lastInput: "09:40:00"), // clamp em 09:45
            Ev(8, PipelineEventTypes.SystemSuspend, "10:00:00", sid: null),
            Ev(9, PipelineEventTypes.SystemResume, "11:00:00", sid: null),
            Ev(10, PipelineEventTypes.ActiveWindowChanged, "11:00:05", process: "a.exe"),
            Ev(11, PipelineEventTypes.SessionEnd, "11:30:00"));

        foreach (var lane in intervals.GroupBy(i => i.WindowsSid))
        {
            var ordered = lane.OrderBy(i => i.StartedAt).ToList();
            for (var i = 1; i < ordered.Count; i++)
                Assert.True(ordered[i].StartedAt >= ordered[i - 1].EndedAt,
                    $"sobreposição na lane '{lane.Key}': {ordered[i - 1].State}@{ordered[i - 1].EndedAt:HH:mm:ss} x {ordered[i].State}@{ordered[i].StartedAt:HH:mm:ss}");
        }
        Assert.All(intervals, i => Assert.True(i.EndedAt > i.StartedAt));
    }

    [Fact]
    public void Idempotencia_MesmaEntradaProduzMesmaSaida()
    {
        var events = new[]
        {
            Ev(1, PipelineEventTypes.ActiveWindowChanged, "09:00:00", process: "a.exe"),
            Ev(2, PipelineEventTypes.IdleStart, "09:20:00", lastInput: "09:10:00"),
            Ev(3, PipelineEventTypes.Lock, "09:30:00"),
            Ev(4, PipelineEventTypes.Unlock, "09:40:00"),
            Ev(5, PipelineEventTypes.SessionEnd, "10:00:00")
        };

        var a = IntervalizationEngine.Build(events).Intervals;
        var b = IntervalizationEngine.Build(events).Intervals;
        Assert.Equal(a, b); // records: igualdade estrutural
    }

    // ------------------------------------------------------------ semente e rabo aberto
    [Fact]
    public void Semente_ContinuaIntervaloAbertoDaJanelaAnterior()
    {
        var seed = new LaneSeed(Sid, IntervalStates.Active, At("08:00:00"), "chrome.exe", "Doc");
        var result = IntervalizationEngine.Build(
            [Ev(10, PipelineEventTypes.Lock, "08:30:00")],
            [seed]);

        var active = Assert.Single(result.Intervals, i => i.State == IntervalStates.Active);
        Assert.Equal(At("08:00:00"), active.StartedAt);
        Assert.Equal(At("08:30:00"), active.EndedAt);
        Assert.Equal("chrome.exe", active.ProcessName);
    }

    [Fact]
    public void RaboAberto_EhDevolvidoEmOpenTails_EFechadoNoUltimoEvento()
    {
        var result = IntervalizationEngine.Build(
        [
            Ev(1, PipelineEventTypes.ActiveWindowChanged, "09:00:00", process: "a.exe"),
            Ev(2, PipelineEventTypes.Heartbeat, "09:05:00", hbState: "active")
        ]);

        var tail = Assert.Single(result.OpenTails);
        Assert.Equal(IntervalStates.Active, tail.State);
        Assert.Equal(At("09:00:00"), tail.Since);

        var active = Assert.Single(result.Intervals);
        Assert.Equal(At("09:05:00"), active.EndedAt); // fechado no último evento
    }
}

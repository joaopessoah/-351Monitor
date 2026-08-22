namespace M351.Domain.Intervalization;

/// <summary>
/// Máquina de estados da intervalização (Seção 7.3) — núcleo PURO do pipeline da F2.
/// Entrada: eventos de UM device, ordenados por (occurred_at, seq), timestamps já
/// corrigidos por clock_offset_ms. Saída: intervalos sem sobreposição por lane.
///
/// Regras canônicas implementadas (PROMPT-DESENVOLVIMENTO.md §7.3, §5.8):
///  - lanes por windows_sid; intervalos de máquina (off_clean/no_data) com sid null;
///  - IDLE_START fecha o active RETROATIVAMENTE em last_input_at (N5), com clamp no
///    início do intervalo corrente (nunca duração negativa);
///  - LOCK fecha qualquer intervalo, inclusive idle (lock vence idle);
///  - SESSION_END/AGENT_STOP/SYSTEM_SUSPEND abrem off_clean até evento de retomada
///    (AGENT_START/SYSTEM_RESUME/SESSION_START/UNLOCK);
///  - gap >= 600 s (N7) sem desligamento limpo fecha no ÚLTIMO evento e vira no_data;
///    HEARTBEAT conta como evento (sustenta o intervalo aberto);
///  - EVENTS_DROPPED vira no_data com data_incomplete = true no trecho coberto;
///  - lacuna de seq marca data_incomplete = true nos intervalos afetados;
///  - HEARTBEAT no_session não gera intervalo de usuário (só prova de vida da máquina);
///  - TIME_CHANGED e demais eventos são neutros (não mudam estado);
///  - pós-processamento N20: descarta < 1 s e funde adjacentes idênticos.
///
/// Decisões documentadas onde a spec é silenciosa:
///  - UNLOCK e IDLE_END abrem active com o ÚLTIMO app conhecido da lane (o próximo
///    ACTIVE_WINDOW_CHANGED corrige);
///  - HEARTBEAT de sessão com a lane vazia abre intervalo no estado reportado (sem ele,
///    sessões só-heartbeat sumiriam da timeline);
///  - gap N7 e EVENTS_DROPPED geram no_data DE MÁQUINA (sid null): no buraco não se
///    sabe o que a sessão fazia;
///  - no_session NÃO encerra off_clean (spec lista só os 4 eventos de retomada); a
///    presença "ligada sem sessão" é responsabilidade do device_current_state;
///  - seq decrescente = reinstalação do agente (reset do AUTOINCREMENT), não lacuna;
///  - o intervalo corrente ao fim dos eventos é fechado no último evento e devolvido
///    em OpenTails — o rebuild seguinte (heartbeats re-sujam o cursor) o re-estende.
/// </summary>
public sealed class IntervalizationEngine
{
    public static readonly TimeSpan DefaultGap = TimeSpan.FromSeconds(600); // N7
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(1);  // N20

    public static IntervalizationResult Build(
        IReadOnlyList<PipelineEvent> events,
        IReadOnlyList<LaneSeed>? seeds = null,
        long? seqBeforeWindow = null,
        DateTimeOffset? windowStart = null,
        TimeSpan? gapThreshold = null)
    {
        var engine = new IntervalizationEngine(gapThreshold ?? DefaultGap);
        engine.Seed(seeds ?? []);
        foreach (var e in events) engine.Apply(e);
        var tails = engine.Finish();
        var flagged = MarkSeqGaps(engine._output, events, seqBeforeWindow, windowStart);
        var polished = PostProcess(flagged);
        return new IntervalizationResult(polished, tails);
    }

    // ------------------------------------------------------------ estado interno
    private sealed class Lane
    {
        public string? OpenState;
        public DateTimeOffset OpenSince;
        public string? OpenProcess;
        public string? OpenTitle;
        public DateTimeOffset? LastEnd;          // invariante de não-sobreposição
        public string? LastKnownProcess;         // p/ IDLE_END/UNLOCK reabrirem active
        public string? LastKnownTitle;
    }

    private readonly TimeSpan _gap;
    private readonly Dictionary<string, Lane> _lanes = new();
    private readonly List<BuiltInterval> _output = [];

    private bool _machineOff;
    private DateTimeOffset _machineOffSince;
    private DateTimeOffset? _machineLastEnd;
    private DateTimeOffset? _lastEventAt;
    private DateTimeOffset? _retroFloor; // fim do último buraco (no_data): aberturas retroativas não o invadem

    private IntervalizationEngine(TimeSpan gap) => _gap = gap;

    private void Seed(IReadOnlyList<LaneSeed> seeds)
    {
        foreach (var s in seeds)
        {
            if (s.WindowsSid is null)
            {
                if (s.State == IntervalStates.OffClean)
                {
                    _machineOff = true;
                    _machineOffSince = s.Since;
                }
                // semente no_data não se re-estende: o gap é recalculado dos eventos
                continue;
            }
            var lane = GetLane(s.WindowsSid);
            lane.OpenState = s.State;
            lane.OpenSince = s.Since;
            lane.OpenProcess = s.ProcessName;
            lane.OpenTitle = s.WindowTitle;
            lane.LastKnownProcess = s.ProcessName;
            lane.LastKnownTitle = s.WindowTitle;
        }
        // _lastEventAt NÃO é semeado: o gap N7 na borda da janela é detectado pelos
        // próprios eventos relidos (R recua 1 h+); semear com o início do intervalo
        // aberto geraria no_data espúrio entre o início da semente e o 1º evento.
    }

    private Lane GetLane(string sid)
    {
        if (!_lanes.TryGetValue(sid, out var lane)) _lanes[sid] = lane = new Lane();
        return lane;
    }

    // ------------------------------------------------------------ aplicação de evento
    private void Apply(PipelineEvent e)
    {
        // Gap N7: >= 600 s sem evento e sem desligamento limpo => fecha no último evento
        // e registra no_data até este. Com a máquina off (off_clean), gap é o esperado.
        if (_lastEventAt is { } last && !_machineOff && e.OccurredAt - last >= _gap)
        {
            CloseAllUserLanes(last);
            EmitMachine(IntervalStates.NoData, last, e.OccurredAt, dataIncomplete: false);
            _retroFloor = e.OccurredAt;
        }

        switch (e.EventType)
        {
            case PipelineEventTypes.ActiveWindowChanged when e.WindowsSid is not null:
            {
                EndMachineOff(e.OccurredAt); // defensivo: atividade de usuário implica máquina ligada
                var lane = GetLane(e.WindowsSid);
                CloseLane(e.WindowsSid, lane, e.OccurredAt);
                OpenLane(lane, IntervalStates.Active, e.OccurredAt, e.ProcessName, e.WindowTitle);
                lane.LastKnownProcess = e.ProcessName;
                lane.LastKnownTitle = e.WindowTitle;
                break;
            }

            case PipelineEventTypes.IdleStart when e.WindowsSid is not null:
            {
                var lane = GetLane(e.WindowsSid);
                if (lane.OpenState == IntervalStates.Locked || lane.OpenState == IntervalStates.Idle)
                    break; // lock vence idle; idle repetido é ruído

                // N5: fecha retroativamente em last_input_at, clampado para nunca gerar
                // duração negativa, sobreposição com o intervalo anterior da lane, nem
                // invasão de um buraco no_data já declarado.
                var floor = lane.OpenState is not null ? lane.OpenSince : (lane.LastEnd ?? e.OccurredAt);
                if (_retroFloor is { } rf && rf > floor) floor = rf;
                var point = Clamp(e.LastInputAt ?? e.OccurredAt, floor, e.OccurredAt);
                CloseLane(e.WindowsSid, lane, point);
                OpenLane(lane, IntervalStates.Idle, point, null, null);
                break;
            }

            case PipelineEventTypes.IdleEnd when e.WindowsSid is not null:
            {
                var lane = GetLane(e.WindowsSid);
                if (lane.OpenState == IntervalStates.Locked) break; // unlock é quem destranca
                CloseLane(e.WindowsSid, lane, e.OccurredAt);
                OpenLane(lane, IntervalStates.Active, e.OccurredAt, lane.LastKnownProcess, lane.LastKnownTitle);
                break;
            }

            case PipelineEventTypes.Lock when e.WindowsSid is not null:
            {
                var lane = GetLane(e.WindowsSid);
                CloseLane(e.WindowsSid, lane, e.OccurredAt); // inclusive idle: lock vence idle
                OpenLane(lane, IntervalStates.Locked, e.OccurredAt, null, null);
                break;
            }

            case PipelineEventTypes.Unlock when e.WindowsSid is not null:
            {
                EndMachineOff(e.OccurredAt); // UNLOCK é evento de retomada (§7.3)
                var lane = GetLane(e.WindowsSid);
                CloseLane(e.WindowsSid, lane, e.OccurredAt);
                OpenLane(lane, IntervalStates.Active, e.OccurredAt, lane.LastKnownProcess, lane.LastKnownTitle);
                break;
            }

            case PipelineEventTypes.SessionStart:
                EndMachineOff(e.OccurredAt);
                break;

            case PipelineEventTypes.SessionEnd:
            {
                if (e.WindowsSid is not null && _lanes.TryGetValue(e.WindowsSid, out var lane))
                    CloseLane(e.WindowsSid, lane, e.OccurredAt);
                // off_clean só quando não resta nenhuma outra sessão aberta (FUS)
                if (_lanes.Values.All(l => l.OpenState is null)) OpenMachineOff(e.OccurredAt);
                break;
            }

            case PipelineEventTypes.AgentStop:
            case PipelineEventTypes.SystemSuspend:
                CloseAllUserLanes(e.OccurredAt);
                OpenMachineOff(e.OccurredAt);
                break;

            case PipelineEventTypes.AgentStart:
            case PipelineEventTypes.SystemResume:
                EndMachineOff(e.OccurredAt);
                break;

            case PipelineEventTypes.Heartbeat:
            {
                if (e.HeartbeatState == "no_session" || e.WindowsSid is null)
                    break; // prova de vida da máquina: sustenta (via _lastEventAt), não intervala

                EndMachineOff(e.OccurredAt); // sessão viva implica máquina ligada
                var lane = GetLane(e.WindowsSid);
                if (lane.OpenState is null)
                {
                    // sessão que só heartbeata (ex.: boot sem AWC ainda): abre no estado reportado
                    var state = e.HeartbeatState switch
                    {
                        "idle" => IntervalStates.Idle,
                        "locked" => IntervalStates.Locked,
                        _ => IntervalStates.Active
                    };
                    var proc = state == IntervalStates.Active ? (e.ProcessName ?? lane.LastKnownProcess) : null;
                    OpenLane(lane, state, e.OccurredAt, proc, state == IntervalStates.Active ? lane.LastKnownTitle : null);
                }
                break;
            }

            case PipelineEventTypes.EventsDropped:
            {
                // trecho coberto vira no_data com data_incomplete = true (§7.3)
                var from = e.OldestDroppedAt ?? _lastEventAt ?? e.OccurredAt;
                if (from > e.OccurredAt) from = e.OccurredAt;
                CloseAllUserLanes(from);
                if (!_machineOff) EmitMachine(IntervalStates.NoData, from, e.OccurredAt, dataIncomplete: true);
                _retroFloor = e.OccurredAt;
                break;
            }

            // TIME_CHANGED, NOTICE_ACK, POLICY_APPLIED, AGENT_TAMPER, AGENT_ERROR, UPDATE_FAILED
            // e desconhecidos: neutros — não mudam estado, só sustentam o intervalo (via
            // _lastEventAt). Falha de update é saúde do agente, não presença de ninguém.
        }

        if (_lastEventAt is null || e.OccurredAt > _lastEventAt) _lastEventAt = e.OccurredAt;
    }

    private IReadOnlyList<LaneSeed> Finish()
    {
        var tails = new List<LaneSeed>();
        if (_lastEventAt is not { } last) return tails;

        foreach (var (sid, lane) in _lanes)
        {
            if (lane.OpenState is null) continue;
            tails.Add(new LaneSeed(sid, lane.OpenState, lane.OpenSince, lane.OpenProcess, lane.OpenTitle));
            CloseLane(sid, lane, last); // fecha no último evento; próximo rebuild re-estende
        }
        if (_machineOff)
        {
            tails.Add(new LaneSeed(null, IntervalStates.OffClean, _machineOffSince, null, null));
            EmitMachine(IntervalStates.OffClean, _machineOffSince, last, dataIncomplete: false);
            _machineOff = false;
        }
        return tails;
    }

    // ------------------------------------------------------------ primitivas
    private static DateTimeOffset Clamp(DateTimeOffset value, DateTimeOffset min, DateTimeOffset max)
        => value < min ? min : value > max ? max : value;

    private void OpenLane(Lane lane, string state, DateTimeOffset since, string? process, string? title)
    {
        lane.OpenState = state;
        lane.OpenSince = lane.LastEnd is { } end && since < end ? end : since;
        lane.OpenProcess = process;
        lane.OpenTitle = title;
    }

    private void CloseLane(string sid, Lane lane, DateTimeOffset at)
    {
        if (lane.OpenState is null) return;
        if (at > lane.OpenSince)
        {
            _output.Add(new BuiltInterval
            {
                WindowsSid = sid,
                StartedAt = lane.OpenSince,
                EndedAt = at,
                State = lane.OpenState,
                ProcessName = lane.OpenState == IntervalStates.Active ? lane.OpenProcess : null,
                WindowTitle = lane.OpenState == IntervalStates.Active ? lane.OpenTitle : null
            });
            lane.LastEnd = at;
        }
        else if (lane.LastEnd is null || lane.OpenSince > lane.LastEnd)
        {
            lane.LastEnd = lane.OpenSince; // intervalo de duração zero: descartado
        }
        lane.OpenState = null;
        lane.OpenProcess = null;
        lane.OpenTitle = null;
    }

    private void CloseAllUserLanes(DateTimeOffset at)
    {
        foreach (var (sid, lane) in _lanes) CloseLane(sid, lane, at);
    }

    private void OpenMachineOff(DateTimeOffset at)
    {
        if (_machineOff) return;
        _machineOff = true;
        _machineOffSince = _machineLastEnd is { } end && at < end ? end : at;
    }

    private void EndMachineOff(DateTimeOffset at)
    {
        if (!_machineOff) return;
        EmitMachine(IntervalStates.OffClean, _machineOffSince, at, dataIncomplete: false);
        _machineOff = false;
    }

    private void EmitMachine(string state, DateTimeOffset from, DateTimeOffset to, bool dataIncomplete)
    {
        if (_machineLastEnd is { } end && from < end) from = end;
        if (to <= from) return;
        _output.Add(new BuiltInterval
        {
            WindowsSid = null,
            StartedAt = from,
            EndedAt = to,
            State = state,
            DataIncomplete = dataIncomplete
        });
        _machineLastEnd = to;
    }

    // ------------------------------------------------------------ lacuna de seq (§7.3)
    private static List<BuiltInterval> MarkSeqGaps(
        List<BuiltInterval> intervals,
        IReadOnlyList<PipelineEvent> events,
        long? seqBeforeWindow,
        DateTimeOffset? windowStart)
    {
        var ranges = new List<(DateTimeOffset From, DateTimeOffset To)>();

        if (seqBeforeWindow is { } before && events.Count > 0 && events[0].Seq > before + 1 && windowStart is { } ws)
            ranges.Add((ws, events[0].OccurredAt));

        for (var i = 1; i < events.Count; i++)
        {
            // seq decrescente = reset (reinstalação do agente), não lacuna
            if (events[i].Seq > events[i - 1].Seq + 1)
                ranges.Add((events[i - 1].OccurredAt, events[i].OccurredAt));
        }

        if (ranges.Count == 0) return intervals;
        return intervals
            .Select(iv => ranges.Any(r => iv.StartedAt < r.To && iv.EndedAt > r.From)
                ? iv with { DataIncomplete = true }
                : iv)
            .ToList();
    }

    // ------------------------------------------------------------ pós-processamento N20
    private static List<BuiltInterval> PostProcess(List<BuiltInterval> intervals)
    {
        var result = new List<BuiltInterval>();
        foreach (var group in intervals
                     .OrderBy(i => i.StartedAt).ThenBy(i => i.EndedAt)
                     .GroupBy(i => i.WindowsSid))
        {
            BuiltInterval? pending = null;
            foreach (var iv in group)
            {
                if (pending is not null &&
                    pending.EndedAt == iv.StartedAt &&
                    pending.State == iv.State &&
                    pending.ProcessName == iv.ProcessName &&
                    pending.WindowTitle == iv.WindowTitle)
                {
                    pending = pending with
                    {
                        EndedAt = iv.EndedAt,
                        DataIncomplete = pending.DataIncomplete || iv.DataIncomplete
                    };
                    continue;
                }
                if (pending is not null && pending.Duration >= MinDuration) result.Add(pending);
                pending = iv;
            }
            if (pending is not null && pending.Duration >= MinDuration) result.Add(pending);
        }
        return result.OrderBy(i => i.StartedAt).ThenBy(i => i.WindowsSid).ToList();
    }
}

public sealed record IntervalizationResult(
    IReadOnlyList<BuiltInterval> Intervals,
    IReadOnlyList<LaneSeed> OpenTails);

using M351.Agent.Core.Collectors;
using Xunit;

namespace M351.Agent.Tests;

public class IdleTrackerTests
{
    private DateTimeOffset _now = new(2026, 6, 9, 14, 31, 40, TimeSpan.Zero);

    [Fact]
    public void Cruzar_limiar_emite_IDLE_START_com_last_input_at_retroativo()
    {
        var tracker = new IdleTracker(300, () => _now);

        // 301 s sem input às 14:31:40 → último input real foi 14:26:39
        var transition = tracker.Sample(301_000);

        Assert.NotNull(transition);
        Assert.Equal(IdleTransitionKind.Start, transition!.Kind);
        Assert.Equal(_now.AddMilliseconds(-301_000), transition.LastInputAt);
        Assert.True(tracker.IsIdle);
    }

    [Fact]
    public void Cenario_do_spec_idle_retroativo()
    {
        // spec 5.4: IDLE_START às 14:31:40.000 com last_input_at 14:26:40.000 (limiar 300 s)
        var tracker = new IdleTracker(300, () => _now);
        var transition = tracker.Sample(300_000);

        Assert.NotNull(transition);
        Assert.Equal(new DateTimeOffset(2026, 6, 9, 14, 26, 40, TimeSpan.Zero), transition!.LastInputAt);
    }

    [Fact]
    public void Abaixo_do_limiar_nao_emite_nada()
    {
        var tracker = new IdleTracker(300, () => _now);
        Assert.Null(tracker.Sample(299_000));
        Assert.False(tracker.IsIdle);
    }

    [Fact]
    public void Continuar_ocioso_nao_reemite_IDLE_START()
    {
        var tracker = new IdleTracker(300, () => _now);
        Assert.NotNull(tracker.Sample(301_000));

        _now = _now.AddSeconds(5);
        Assert.Null(tracker.Sample(306_000));
        Assert.True(tracker.IsIdle);
    }

    [Fact]
    public void Voltar_atividade_emite_IDLE_END_com_duracao_do_ciclo()
    {
        var tracker = new IdleTracker(300, () => _now);
        tracker.Sample(301_000); // idle desde now-301s

        // 99 s depois o usuário mexe o mouse (idle_ms volta a ~2 s)
        _now = _now.AddSeconds(99);
        var transition = tracker.Sample(2_000);

        Assert.NotNull(transition);
        Assert.Equal(IdleTransitionKind.End, transition!.Kind);
        // duração = (now-2s) - (início-301s) = 301 + 99 - 2 = 398 s
        Assert.Equal(398_000, transition.IdleDurationMs);
        Assert.False(tracker.IsIdle);
    }

    [Fact]
    public void Limiar_da_config_e_aplicado_dinamicamente()
    {
        var tracker = new IdleTracker(300, () => _now);
        Assert.Null(tracker.Sample(120_000)); // 2 min < 5 min

        tracker.UpdateThreshold(60); // config nova: 60 s
        var transition = tracker.Sample(120_000);
        Assert.NotNull(transition);
        Assert.Equal(IdleTransitionKind.Start, transition!.Kind);
    }

    [Fact]
    public void Duracao_nunca_negativa()
    {
        var tracker = new IdleTracker(300, () => _now);
        tracker.Sample(301_000);
        var transition = tracker.Sample(400_000); // idle_ms maior que o ciclo (caso degenerado)
        Assert.Null(transition); // continua idle — sem transição
        var end = tracker.Sample(0);
        Assert.NotNull(end);
        Assert.True(end!.IdleDurationMs >= 0);
    }
}

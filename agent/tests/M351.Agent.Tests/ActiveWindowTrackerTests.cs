using M351.Agent.Core.Collectors;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Events;
using M351.Agent.Core.Privacy;
using M351.Agent.Tests.Support;
using Xunit;

namespace M351.Agent.Tests;

public class ActiveWindowTrackerTests
{
    private long _mono = 100_000;
    private DateTimeOffset _now = new(2026, 6, 9, 14, 0, 0, TimeSpan.Zero);
    private readonly EventFactory _factory;
    private readonly ActiveWindowTracker _tracker;

    public ActiveWindowTrackerTests()
    {
        var config = AgentConfig.FactoryDefault();
        config.WindowTitlePolicy = TitlePolicies.Full;
        _factory = new EventFactory(TestEvents.BootId, () => _now, () => _mono);
        _tracker = new ActiveWindowTracker(new TitleMasker(), config, () => _mono, () => _now);
    }

    private void Advance(long ms)
    {
        _mono += ms;
        _now = _now.AddMilliseconds(ms);
    }

    private TrackerResult? Sample(string process, string? title) =>
        _tracker.Sample(new ForegroundSample(process, $@"C:\{process}", null, title),
            data => _factory.Create(EventTypes.ActiveWindowChanged, data, 1, "S-1", @"ACME\u"));

    [Fact]
    public void Primeira_amostra_emite_evento()
    {
        var result = Sample("chrome.exe", "Inbox");
        Assert.NotNull(result?.NewEvent);
        Assert.Equal(EventTypes.ActiveWindowChanged, result!.NewEvent!.Type);
        Assert.Equal("chrome.exe", result.NewEvent.Data.GetProperty("process_name").GetString());
    }

    [Fact]
    public void Mesma_janela_nao_reemite_dedupe_N1()
    {
        Sample("chrome.exe", "Inbox");
        Advance(5_000);
        Assert.Null(Sample("chrome.exe", "Inbox"));
        Advance(5_000);
        Assert.Null(Sample("chrome.exe", "Inbox"));
    }

    [Fact]
    public void Mudanca_de_processo_emite_novo_evento()
    {
        Sample("chrome.exe", "Inbox");
        Advance(5_000);
        var result = Sample("excel.exe", "Planilha");
        Assert.NotNull(result?.NewEvent);
        Assert.Equal("excel.exe", result!.NewEvent!.Data.GetProperty("process_name").GetString());
    }

    [Fact]
    public void Anti_flapping_N16_titulo_muda_em_menos_de_10s_atualiza_ultimo_evento()
    {
        var first = Sample("spotify.exe", "Música A");
        Advance(5_000); // < 10 s
        var result = Sample("spotify.exe", "Música B");

        Assert.NotNull(result);
        Assert.Null(result!.NewEvent);
        Assert.NotNull(result.UpdatedEvent);
        Assert.Equal(first!.NewEvent!.EventId, result.UpdatedEvent!.EventId); // mesmo event_id
        Assert.Equal(first.NewEvent.OccurredAt, result.UpdatedEvent.OccurredAt); // occurred_at imutável
        Assert.Equal("Música B", result.UpdatedEvent.Data.GetProperty("window_title").GetString());
    }

    [Fact]
    public void Titulo_muda_apos_10s_emite_evento_novo()
    {
        Sample("spotify.exe", "Música A");
        Advance(11_000); // > 10 s
        var result = Sample("spotify.exe", "Música B");
        Assert.NotNull(result?.NewEvent);
        Assert.Null(result!.UpdatedEvent);
    }

    [Fact]
    public void Rate_limit_N17_1_por_segundo_coalesce_e_depois_emite_EVENTS_DROPPED()
    {
        Sample("a.exe", "1");
        Advance(500); // < 1 s
        Assert.Null(Sample("b.exe", "2")); // coalescido

        Advance(5_000);
        var result = Sample("c.exe", "3");
        Assert.NotNull(result?.NewEvent);
        Assert.NotNull(result!.Drops);
        Assert.Equal(1, result.Drops!.Count);
    }

    [Fact]
    public void Rate_limit_N17_600_por_hora()
    {
        // 600 emissões espaçadas de 5 s (3.000 s < 1 h)
        for (var i = 0; i < 600; i++)
        {
            var r = Sample($"app{i}.exe", "t");
            Assert.NotNull(r?.NewEvent);
            Advance(5_000);
        }

        // a 601ª dentro da mesma hora é coalescida
        Assert.Null(Sample("estouro.exe", "t"));
    }

    [Fact]
    public void Processo_ignorado_emite_privado()
    {
        var result = Sample("keepass.exe", "Cofre");
        Assert.NotNull(result?.NewEvent);
        var data = result!.NewEvent!.Data;
        Assert.Equal(TitleMasker.PrivateProcessName, data.GetProperty("process_name").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, data.GetProperty("window_title").ValueKind);
    }

    [Fact]
    public void Amostra_nula_nao_emite_nem_quebra()
    {
        Assert.Null(_tracker.Sample(null, _ => throw new InvalidOperationException("não deve criar evento")));
    }
}

using System.Text.Json;
using M351.Agent.Core.Contracts;
using M351.Agent.Core.Logging;
using M351.Agent.Core.Net;
using M351.Agent.Core.Security;
using M351.Agent.Core.Storage;
using M351.Agent.Tests.Support;
using Xunit;

namespace M351.Agent.Tests;

/// <summary>Parsing e processamento do ack — Seção 5.5 (exemplo verbatim do spec).</summary>
public class AckProcessorTests
{
    private const string SpecAckJson = """
        {
          "accepted": 3,
          "duplicates": 1,
          "rejected": [
            { "event_id": "01976f2b-c890-7a66-8e9f-3c4d5e6f7a8b", "reason": "timestamp_in_future" }
          ],
          "server_time": "2026-06-09T14:32:07.852Z",
          "config_version": 5,
          "config": {
            "heartbeat_sec": 60,
            "active_window_poll_sec": 5,
            "idle_threshold_sec": 300,
            "window_title_policy": "MASKED_PATTERNS",
            "masked_patterns": ["(?i)senha", "(?i)\\bbanco\\b", "\\d{3}\\.\\d{3}\\.\\d{3}-\\d{2}"],
            "ignored_processes": ["keepass.exe", "1password.exe", "bitwarden.exe", "logonui.exe", "lockapp.exe", "consent.exe"],
            "collection_window": { "mode": "ALWAYS", "days": null, "start": null, "end": null },
            "transparency_url": "https://app.exemplo.com.br/transparencia/acme"
          },
          "commands": [
            { "id": "01976f2c-0000-7aaa-b111-00000000c0de", "type": "UNENROLL", "payload": {} }
          ]
        }
        """;

    private static AckResponse ParseSpecAck() =>
        JsonSerializer.Deserialize(SpecAckJson, AgentJsonContext.Default.AckResponse)!;

    [Fact]
    public void Ack_do_spec_parseia_todos_os_campos()
    {
        var ack = ParseSpecAck();

        Assert.Equal(3, ack.Accepted);
        Assert.Equal(1, ack.Duplicates);
        var rejected = Assert.Single(ack.Rejected);
        Assert.Equal("01976f2b-c890-7a66-8e9f-3c4d5e6f7a8b", rejected.EventId);
        Assert.Equal("timestamp_in_future", rejected.Reason);
        Assert.Equal("2026-06-09T14:32:07.852Z", ack.ServerTime);
        Assert.Equal(5, ack.ConfigVersion);

        Assert.NotNull(ack.Config);
        Assert.Equal(60, ack.Config!.HeartbeatSec);
        Assert.Equal(5, ack.Config.ActiveWindowPollSec);
        Assert.Equal(300, ack.Config.IdleThresholdSec);
        Assert.Equal("MASKED_PATTERNS", ack.Config.WindowTitlePolicy);
        Assert.Equal(3, ack.Config.MaskedPatterns.Count);
        Assert.Equal(6, ack.Config.IgnoredProcesses.Count);
        Assert.Equal("ALWAYS", ack.Config.CollectionWindow.Mode);
        Assert.Null(ack.Config.CollectionWindow.Days);
        Assert.Equal("https://app.exemplo.com.br/transparencia/acme", ack.Config.TransparencyUrl);

        var command = Assert.Single(ack.Commands!);
        Assert.Equal(CommandTypes.Unenroll, command.Type);
    }

    [Fact]
    public void Ack_sem_config_e_sem_commands_parseia_com_null()
    {
        var ack = JsonSerializer.Deserialize(
            """{"accepted":0,"duplicates":0,"rejected":[],"server_time":"2026-06-09T14:32:07.852Z","config_version":5,"config":null,"commands":null}""",
            AgentJsonContext.Default.AckResponse)!;

        Assert.Null(ack.Config);
        Assert.Null(ack.Commands);
    }

    private static (TempQueue Temp, AgentStateStore State, AckProcessor Processor) Build()
    {
        var temp = new TempQueue();
        var state = new AgentStateStore(temp.Queue, new PlaintextSecretProtector());
        var processor = new AckProcessor(temp.Queue, state, TestEvents.Factory(), new NullLogSink());
        return (temp, state, processor);
    }

    [Fact]
    public void Config_nova_e_persistida_e_POLICY_APPLIED_emitido()
    {
        var (temp, state, processor) = Build();
        using var _ = temp;
        state.SaveConfig(AgentConfig.FactoryDefault(), 4);

        var ack = ParseSpecAck();
        ack.Commands = null; // só a config neste teste
        var configApplied = false;
        processor.ConfigApplied += _ => configApplied = true;

        processor.Process(ack);

        Assert.Equal(5, state.ConfigVersion);
        Assert.Equal("https://app.exemplo.com.br/transparencia/acme", state.Config.TransparencyUrl);
        Assert.True(configApplied);

        var policyApplied = temp.Queue.PeekBatch(10).Single(e => e.Type == EventTypes.PolicyApplied);
        Assert.Equal(5, policyApplied.Data.GetProperty("config_version").GetInt32());
    }

    [Fact]
    public void Ack_sem_config_nao_emite_POLICY_APPLIED()
    {
        var (temp, _, processor) = Build();
        using var _1 = temp;

        processor.Process(new AckResponse { Accepted = 1, ConfigVersion = 5, Config = null });

        Assert.DoesNotContain(temp.Queue.PeekBatch(10), e => e.Type == EventTypes.PolicyApplied);
    }

    [Fact]
    public void UNENROLL_descarta_fila_esquece_token_e_para_coleta()
    {
        var (temp, state, processor) = Build();
        using var _ = temp;
        state.DeviceId = "01976f00-aaaa-7bbb-8ccc-dddddddddddd";
        state.DeviceToken = "dt_secreto";
        state.ServerUrl = "http://localhost:5080";
        var factory = TestEvents.Factory();
        for (var i = 0; i < 7; i++) temp.Queue.Enqueue(TestEvents.Heartbeat(factory));

        var unenrolled = false;
        processor.Unenrolled += () => unenrolled = true;

        processor.Process(new AckResponse
        {
            Accepted = 0,
            ConfigVersion = 5,
            Commands = [new AgentCommand { Id = "x", Type = "UNENROLL" }]
        });

        Assert.True(unenrolled);
        Assert.Equal(0, temp.Queue.TotalCount); // fila descartada
        Assert.Null(state.DeviceToken);         // token esquecido
        Assert.Null(state.DeviceId);
        Assert.True(state.Unenrolled);
        Assert.False(state.IsEnrolled);
    }

    [Fact]
    public void Comando_desconhecido_e_ignorado_sem_efeitos()
    {
        var (temp, state, processor) = Build();
        using var _ = temp;
        state.DeviceToken = "dt_x";
        temp.Queue.Enqueue(TestEvents.Heartbeat(TestEvents.Factory()));

        processor.Process(new AckResponse
        {
            ConfigVersion = 1,
            Commands = [new AgentCommand { Id = "y", Type = "ROTATE_TOKEN" }] // v1.1: sem handler
        });

        Assert.Equal(1, temp.Queue.TotalCount);
        Assert.Equal("dt_x", state.DeviceToken);
    }
}

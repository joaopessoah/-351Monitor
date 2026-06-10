using M351.Agent.Core.Contracts;
using M351.Agent.Core.Security;
using M351.Agent.Core.Storage;
using M351.Agent.Tests.Support;
using Xunit;

namespace M351.Agent.Tests;

public class AgentStateStoreTests
{
    [Fact]
    public void Boot_id_novo_por_boot_e_estavel_no_mesmo_boot()
    {
        using var temp = new TempQueue();
        var state = new AgentStateStore(temp.Queue, new PlaintextSecretProtector());
        var now = new DateTimeOffset(2026, 6, 9, 8, 0, 0, TimeSpan.Zero);

        var (boot1, reason1) = state.InitializeBoot(now, monoMs: 60_000);
        Assert.Equal("install", reason1); // primeira execução

        // mesmo boot (boot time igual), restart do serviço com shutdown limpo
        state.MarkCleanShutdown();
        var (boot2, reason2) = state.InitializeBoot(now.AddMinutes(10), monoMs: 660_000);
        Assert.Equal(boot1, boot2);
        Assert.Equal("service_restart", reason2);

        // boot novo (uptime zerou) com shutdown limpo anterior
        state.MarkCleanShutdown();
        var (boot3, reason3) = state.InitializeBoot(now.AddHours(2), monoMs: 30_000);
        Assert.NotEqual(boot2, boot3);
        Assert.Equal("boot", reason3);
    }

    [Fact]
    public void Sem_flag_de_shutdown_limpo_vira_crash_recovery()
    {
        using var temp = new TempQueue();
        var state = new AgentStateStore(temp.Queue, new PlaintextSecretProtector());
        var now = new DateTimeOffset(2026, 6, 9, 8, 0, 0, TimeSpan.Zero);

        state.InitializeBoot(now, 60_000);
        // sem MarkCleanShutdown (queda de energia / kill)
        var (_, reason) = state.InitializeBoot(now.AddHours(1), 30_000);
        Assert.Equal("crash_recovery", reason);
    }

    [Fact]
    public void Config_persistida_sobrevive_a_reabertura()
    {
        using var temp = new TempQueue();
        var state = new AgentStateStore(temp.Queue, new PlaintextSecretProtector());
        var config = AgentConfig.FactoryDefault();
        config.IdleThresholdSec = 600;
        state.SaveConfig(config, 7);

        temp.Reopen();
        var state2 = new AgentStateStore(temp.Queue, new PlaintextSecretProtector());

        Assert.Equal(7, state2.ConfigVersion);
        Assert.Equal(600, state2.Config.IdleThresholdSec);
    }

    [Fact]
    public void IsEnrolled_exige_device_token_e_servidor()
    {
        using var temp = new TempQueue();
        var state = new AgentStateStore(temp.Queue, new PlaintextSecretProtector());
        Assert.False(state.IsEnrolled);

        state.DeviceId = "d1";
        state.DeviceToken = "dt_x";
        state.ServerUrl = "http://localhost:5080";
        Assert.True(state.IsEnrolled);

        state.ForgetIdentity();
        Assert.False(state.IsEnrolled);
        Assert.True(state.Unenrolled);
    }
}

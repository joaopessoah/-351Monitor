using M351.Agent.Core.Logging;
using M351.Agent.Core.Security;
using M351.Agent.Core.Storage;
using M351.Agent.Tests.Support;
using MonitorAgentService;
using Xunit;

namespace M351.Agent.Tests;

public class UpdateFlagTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"m351-flag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Write_set_consume_roundtrip()
    {
        var dir = NewDir();
        try
        {
            Assert.False(UpdateFlag.IsSet(dir));

            UpdateFlag.Write(dir, new NullLogSink());
            Assert.True(UpdateFlag.IsSet(dir));
            Assert.True(File.Exists(UpdateFlag.PathFor(dir)));

            // IsSet NAO consome (o stop precisa ver e o start consome).
            Assert.True(UpdateFlag.IsSet(dir));

            Assert.True(UpdateFlag.Consume(dir, new NullLogSink()));
            Assert.False(UpdateFlag.IsSet(dir));

            // Segundo consume e no-op (idempotente).
            Assert.False(UpdateFlag.Consume(dir, new NullLogSink()));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- Precedencia do start_reason (Secao 6.7): update > install > crash_recovery > boot > service_restart ---
    // ResolveStopReason e privado no servico; a decisao de start_reason mora em
    // AgentStateStore.InitializeBoot(updateDetected) — testada aqui diretamente.

    [Fact]
    public void Update_detectado_vence_crash_recovery()
    {
        using var temp = new TempQueue();
        var state = new AgentStateStore(temp.Queue, new PlaintextSecretProtector());
        var now = new DateTimeOffset(2026, 6, 15, 8, 0, 0, TimeSpan.Zero);

        state.InitializeBoot(now, 60_000); // primeiro start (install)
        // sem MarkCleanShutdown: um start "normal" daqui seria crash_recovery…
        var (_, reason) = state.InitializeBoot(now.AddMinutes(5), 360_000, updateDetected: true);

        Assert.Equal("update", reason); // …mas a sentinela .update vence
    }

    [Fact]
    public void Update_detectado_vence_install_no_primeiro_start()
    {
        using var temp = new TempQueue();
        var state = new AgentStateStore(temp.Queue, new PlaintextSecretProtector());
        var now = new DateTimeOffset(2026, 6, 15, 8, 0, 0, TimeSpan.Zero);

        var (_, reason) = state.InitializeBoot(now, 60_000, updateDetected: true);

        Assert.Equal("update", reason);
    }

    [Fact]
    public void Sem_update_mantem_a_logica_normal()
    {
        using var temp = new TempQueue();
        var state = new AgentStateStore(temp.Queue, new PlaintextSecretProtector());
        var now = new DateTimeOffset(2026, 6, 15, 8, 0, 0, TimeSpan.Zero);

        var (_, r1) = state.InitializeBoot(now, 60_000, updateDetected: false);
        Assert.Equal("install", r1);

        state.MarkCleanShutdown();
        var (_, r2) = state.InitializeBoot(now.AddMinutes(10), 660_000, updateDetected: false);
        Assert.Equal("service_restart", r2);
    }
}

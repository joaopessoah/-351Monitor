using M351.Agent.Core.Contracts;
using M351.Agent.Core.Events;

namespace M351.Agent.Tests.Support;

public static class TestEvents
{
    public static readonly string BootId = Guid.NewGuid().ToString();

    public static EventFactory Factory(DateTimeOffset? fixedNow = null, long mono = 1000) =>
        new(BootId,
            fixedNow is null ? null : () => fixedNow.Value,
            () => mono);

    public static AgentEvent Heartbeat(EventFactory factory) =>
        factory.Create(EventTypes.Heartbeat,
            new HeartbeatData { State = "active", ForegroundProcess = "test.exe", IdleMs = 0, QueueDepth = 0 },
            sessionId: 1, windowsSid: "S-1-5-21-1", windowsUser: @"ACME\teste");
}

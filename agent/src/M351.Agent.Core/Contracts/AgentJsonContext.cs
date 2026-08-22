using System.Text.Json;
using System.Text.Json.Serialization;

namespace M351.Agent.Core.Contracts;

/// <summary>Serialização source-generated (System.Text.Json) — stack obrigatória da Seção 4.</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(AgentEvent))]
[JsonSerializable(typeof(BatchRequest))]
[JsonSerializable(typeof(AckResponse))]
[JsonSerializable(typeof(RejectedEvent))]
[JsonSerializable(typeof(AgentCommand))]
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(CollectionWindow))]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(PipeMessage))]
[JsonSerializable(typeof(AgentStartData))]
[JsonSerializable(typeof(AgentStopData))]
[JsonSerializable(typeof(SessionStartData))]
[JsonSerializable(typeof(ActiveWindowData))]
[JsonSerializable(typeof(IdleStartData))]
[JsonSerializable(typeof(IdleEndData))]
[JsonSerializable(typeof(HeartbeatData))]
[JsonSerializable(typeof(SystemResumeData))]
[JsonSerializable(typeof(TimeChangedData))]
[JsonSerializable(typeof(EventsDroppedData))]
[JsonSerializable(typeof(AgentTamperData))]
[JsonSerializable(typeof(NoticeAckData))]
[JsonSerializable(typeof(PolicyAppliedData))]
[JsonSerializable(typeof(AgentErrorData))]
[JsonSerializable(typeof(UpdateFailedData))]
[JsonSerializable(typeof(JsonElement))]
public partial class AgentJsonContext : JsonSerializerContext
{
}

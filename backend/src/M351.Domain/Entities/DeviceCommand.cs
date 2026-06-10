namespace M351.Domain.Entities;

/// <summary>
/// Tabela device_commands — canal pull via ack do batch; MVP: apenas UNENROLL (Seção 5.5).
/// O servidor marca a entrega (delivered_at) ao incluir o comando no ack; reentrega é idempotente.
/// </summary>
public class DeviceCommand : ITenantEntity
{
    public const string TypeUnenroll = "UNENROLL";

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public string Type { get; set; } = TypeUnenroll;
    public string Payload { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeliveredAt { get; set; }
}

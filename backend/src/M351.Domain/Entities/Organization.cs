namespace M351.Domain.Entities;

/// <summary>Tabela organizations — a organização É o tenant (id = tenant_id das demais tabelas).</summary>
public class Organization
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string Timezone { get; set; } = "America/Sao_Paulo";
    public string? BusinessHours { get; set; }
    public string Plan { get; set; } = "trial";
    public int? DeviceLimit { get; set; }
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

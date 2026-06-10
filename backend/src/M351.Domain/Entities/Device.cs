namespace M351.Domain.Entities;

/// <summary>Tabela devices — estações registradas (a F0 só lista; enroll/ingestão são F1).</summary>
public class Device : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Hostname { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>SHA-256(MachineGuid + serial do BIOS); re-enroll idempotente por (tenant_id, fingerprint).</summary>
    public required string MachineFingerprint { get; set; }

    public string? OsVersion { get; set; }
    public string OsType { get; set; } = "workstation";
    public string? AgentVersion { get; set; }
    public Guid? EnrollmentKeyId { get; set; }

    /// <summary>SHA-256 do device token vigente.</summary>
    public required byte[] TokenHash { get; set; }

    public int ConfigVersion { get; set; } = 1;
    public string[]? Tags { get; set; }
    public string Status { get; set; } = "active";
    public DateTimeOffset? LastSeenAt { get; set; }
    public long ClockOffsetMs { get; set; }
    public int? TzOffsetMin { get; set; }
    public string? TzIana { get; set; }
    public long SeqMax { get; set; }
    public DateTimeOffset? NoticeAckedAt { get; set; }
}

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

    /// <summary>Ultimo AGENT_TAMPER materializado na ingestao (monotonico, igual ao notice_acked_at) — saude F4.4.</summary>
    public DateTimeOffset? LastTamperAt { get; set; }

    /// <summary>Motivo do tamper mais recente: helper_killed | helper_killed_repeatedly | pipe_denied (N19).</summary>
    public string? LastTamperReason { get; set; }

    /// <summary>
    /// Último UPDATE_FAILED materializado na ingestão (monotônico, igual ao last_tamper_at) —
    /// vigilância de rollout: sem isto, a máquina travada num release ruim aparecia apenas como
    /// "versão desatualizada", sem dizer em que etapa a atualização emperrou.
    /// </summary>
    public DateTimeOffset? LastUpdateFailureAt { get; set; }

    /// <summary>Etapa que reprovou na última tentativa: download | hash | signature | install.</summary>
    public string? LastUpdateFailureReason { get; set; }

    /// <summary>Versão que a última tentativa de atualização mirava (to_version do UPDATE_FAILED).</summary>
    public string? LastUpdateTargetVersion { get; set; }

    /// <summary>
    /// F5 — token não adivinhável da página pública do funcionário ("Ver o que minha empresa
    /// vê", antecipação da v1.1): GET /public/t/{token} mostra a política vigente, retenções,
    /// canais do DPO e o status de ciência/operacional deste device, SEM dado pessoal do dia
    /// (o histórico individual autenticado segue sendo decisão de v2+). Preenchido no enroll
    /// e backfillado na migration.
    /// </summary>
    public Guid? TransparencyToken { get; set; }
}

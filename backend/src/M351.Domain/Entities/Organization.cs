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

    // F4.8 — campos de transparência editáveis pelo admin (Seção 8.8), expostos na página
    // pública /transparencia/:slug. Texto livre/data, NUNCA dado pessoal de titular.

    /// <summary>Finalidade declarada do monitoramento (texto livre, exibido na transparência) — null até preenchido.</summary>
    public string? FinalidadeDeclarada { get; set; }

    /// <summary>Contato do DPO/encarregado da controladora (texto livre) — null até preenchido.</summary>
    public string? ContatoDpo { get; set; }

    /// <summary>Data de vigência da política declarada — null até preenchida.</summary>
    public DateOnly? DataVigencia { get; set; }
}

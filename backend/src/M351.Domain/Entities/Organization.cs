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

    /// <summary>
    /// F5 — checklist de primeiros passos dispensado pelo gestor (Seção 8.3 passo 4: card
    /// dispensável na Visão Geral). Estado de UI da ORG (não por usuário, deliberado: o
    /// onboarding é da organização); null = card visível enquanto houver passo pendente.
    /// </summary>
    public DateTimeOffset? OnboardingChecklistDismissedAt { get; set; }

    /// <summary>
    /// F5 — última vez que o digest semanal foi enviado para esta org (idempotência do job
    /// horário: reinício do worker dentro da mesma janela não reenvia).
    /// </summary>
    public DateTimeOffset? LastWeeklyDigestAt { get; set; }

    /// <summary>
    /// F5 — meta semanal AGREGADA de horas ativas da equipe (nunca por pessoa, sem ranking).
    /// null = sem meta. Exibida como barra de progresso na Visão Geral e markLine no gráfico.
    /// </summary>
    public int? GoalWeeklyActiveHours { get; set; }

    /// <summary>
    /// F5 — meta de percentual do tempo em apps relacionados ao trabalho (0 a 100, agregado
    /// da organização). null = sem meta.
    /// </summary>
    public int? GoalWorkRelatedPct { get; set; }
}

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

    /// <summary>
    /// F6, decisão 1 do spec de 07/09/2026 — VOCABULÁRIO dos rótulos de classificação que a
    /// organização vê no portal. Só o rótulo muda; os dados (classification +1/0/−1 e o balde
    /// sem classificação) são os mesmos nos dois conjuntos, então trocar aqui NÃO reagrega nada.
    ///
    ///  - "produtividade" (default): Produtivo / Neutro / Improdutivo / Sem classificação —
    ///    padrão da categoria e o que o site promete;
    ///  - "trabalho": Relacionado ao trabalho / Neutro / Não relacionado ao trabalho /
    ///    Não categorizado — o conjunto neutro, para quem prefere não usar adjetivo.
    ///
    /// O enquadramento "classificação definida pela sua empresa" acompanha os dois: o julgamento
    /// é do cliente sobre APLICATIVOS, nunca sobre pessoas, e estado de máquina (inclusive
    /// ocioso) fica fora deste vocabulário — ocioso NUNCA é improdutivo.
    /// </summary>
    public string ClassificationVocabulary { get; set; } = "produtividade";

    /// <summary>
    /// F6, decisão 5 do spec de 07/09/2026 — OPT-IN da organização para as regras de alerta de
    /// escopo PESSOA (dias longos e atividade fora do horário). Padrão é <c>false</c>: alerta
    /// por EQUIPE é o padrão do produto, e olhar o dia de uma pessoa isolada só acontece se a
    /// CONTROLADORA decidir explicitamente que quer isso — decisão que fica em auditoria
    /// (<see cref="AuditActions.UpdateAlertPrefs"/>).
    ///
    /// Enquanto false, o ManagementAlertService NÃO avalia as duas regras de pessoa: nenhum
    /// alerta de pessoa fica guardado no banco esperando o toggle ser ligado.
    /// </summary>
    public bool PersonAlertsEnabled { get; set; }
}

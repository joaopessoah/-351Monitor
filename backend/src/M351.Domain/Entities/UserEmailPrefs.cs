namespace M351.Domain.Entities;

/// <summary>
/// Tabela user_email_prefs — preferências de e-mail por usuário do portal (F5). Linha
/// AUSENTE = defaults (digest e alertas de frota ligados para Owner/Admin; jornada
/// semanal desligada). Uma tabela única para o digest semanal, os alertas de saúde de
/// frota (exclusivos do plano Pro) e a assinatura do relatório de jornada.
/// </summary>
public class UserEmailPrefs : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Resumo semanal por e-mail (segunda 08h no fuso da org).</summary>
    public bool WeeklyDigest { get; set; } = true;

    /// <summary>Alertas de saúde de frota (device sem comunicação, tamper, ciência pendente).</summary>
    public bool FleetAlerts { get; set; } = true;

    /// <summary>Relatório de jornada da semana anterior por e-mail (segunda 07h, link).</summary>
    public bool JornadaWeekly { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace M351.Infrastructure.AccountHealth;

/// <summary>
/// Sinal de risco detectado numa conta. Cada sinal tem PESO em pontos de risco e um rótulo
/// em PT-BR que vai inteiro para o e-mail e para a coluna de observações do CSV do CRM: o CS
/// precisa saber POR QUE ligar, não só que o número piorou.
/// </summary>
public sealed record AccountHealthSignal(string Code, string Label, int Points);

/// <summary>
/// Resultado de uma apuração: quantas contas foram avaliadas e quais acumularam risco. As duas
/// contagens andam juntas porque "3 em risco" só quer dizer alguma coisa ao lado de "de quantas".
/// </summary>
public sealed record AccountHealthReport(int Evaluated, IReadOnlyList<AccountHealthRow> AtRisk);

/// <summary>
/// Uma conta avaliada pelo <see cref="AccountHealthService"/>. Métricas AGREGADAS por tenant
/// (contagens de dispositivos, datas de último acesso), nada de dado monitorado de pessoa.
/// </summary>
public sealed record AccountHealthRow(
    Guid TenantId,
    string Name,
    string Slug,
    string Plan,
    DateTimeOffset CreatedAt,
    string? ContactName,
    string? ContactEmail,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? LastSeenAt,
    int ActiveDevices,
    int DevicesSeenLast7d,
    int DevicesWithDataCurrent,
    int DevicesWithDataPrevious,
    int ReadActionsLast14d,
    int UncategorizedApps,
    IReadOnlyList<AccountHealthSignal> Signals)
{
    /// <summary>Soma dos pontos de risco, limitada a 100.</summary>
    public int RiskPoints => Math.Min(100, Signals.Sum(s => s.Points));

    /// <summary>Score de SAÚDE (100 = nenhum sinal de risco).</summary>
    public int HealthScore => 100 - RiskPoints;

    /// <summary>Faixa em PT-BR: "crítico" (0 a 50), "atenção" (51 a 75), "saudável" (76 a 100).</summary>
    public string Faixa => HealthScore switch
    {
        <= 50 => "crítico",
        <= 75 => "atenção",
        _ => "saudável",
    };
}

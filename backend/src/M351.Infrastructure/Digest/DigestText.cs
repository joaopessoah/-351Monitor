using System.Globalization;

namespace M351.Infrastructure.Digest;

/// <summary>
/// Vocabulário e formatação COMPARTILHADOS pelo digest do gestor, pelo digest pessoal e
/// pelo PDF do resumo. Um lugar só para que os três digam a mesma coisa com as mesmas
/// palavras.
///
/// REGRAS DE REDAÇÃO (spec, seções 2.1 e 8):
///  - nunca a palavra "ranking", nunca lista de pessoas ordenada por métrica;
///  - nunca linguagem de ponto (entrada, saída, hora extra, banco de horas);
///  - variação é descrita em PONTOS e sem juízo de valor ("3 pontos acima da semana
///    anterior"), jamais como bom ou ruim;
///  - ocioso nunca é chamado de improdutivo.
/// </summary>
public static class DigestText
{
    public static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Duração no formato do portal: 6h00.</summary>
    public static string Hours(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours}h{ts.Minutes:00}";
    }

    /// <summary>Percentual inteiro; "—" quando a métrica não existe (denominador zero).</summary>
    public static string Pct(double? ratio) =>
        ratio is { } value ? $"{Math.Round(value * 100).ToString("0", PtBr)}%" : "—";

    /// <summary>Fatia de um total, em percentual inteiro (0 % quando o total é zero).</summary>
    public static string Share(long part, long total) =>
        total > 0 ? Pct((double)part / total) : "—";

    /// <summary>
    /// Variação do índice EM PONTOS PERCENTUAIS vs a semana anterior. Pontos, não
    /// percentual do percentual: "de 62 % para 65 %" são 3 pontos, não 4,8 %.
    /// </summary>
    public static string IndexDelta(double? current, double? previous)
    {
        if (current is null) return "sem tempo classificado nesta semana";
        if (previous is null) return "sem base de comparação na semana anterior";

        var points = Math.Round((current.Value - previous.Value) * 100);
        return points switch
        {
            > 0 => $"{points.ToString("0", PtBr)} ponto(s) acima da semana anterior",
            < 0 => $"{Math.Abs(points).ToString("0", PtBr)} ponto(s) abaixo da semana anterior",
            _ => "estável vs semana anterior",
        };
    }

    /// <summary>Delta NEUTRO de uma duração vs a semana anterior (sem cor de bom/ruim).</summary>
    public static string HoursDelta(long current, long previous)
    {
        if (previous <= 0) return "sem base anterior";

        var pct = (double)(current - previous) / previous * 100;
        return pct switch
        {
            > 0.5 => $"{pct.ToString("0", PtBr)}% maior que na semana anterior",
            < -0.5 => $"{Math.Abs(pct).ToString("0", PtBr)}% menor que na semana anterior",
            _ => "estável vs semana anterior",
        };
    }

    /// <summary>
    /// Frase de um grupo de alertas vivos. Escopo "person" viaja SEM NOME: o digest do
    /// gestor descreve quantos sinais existem, e o nome só aparece na central de alertas,
    /// que é auditada. Espelha o vocabulário do AlertsController (contexto, nunca juízo).
    /// </summary>
    public static string Alert(AlertGroup group)
    {
        var scope = group.SingleScopeKey is { Length: > 0 } team ? $" na equipe {team}" : Plural(group);
        return group.Kind switch
        {
            "coverage_below" => "Cobertura da classificação abaixo da régua: há tempo ativo em aplicativos sem categoria, e o índice não considera esse tempo",
            "team_active_drop" => $"Queda de tempo ativo por pessoa{scope} vs a média das semanas anteriores",
            "team_unproductive_high" => $"Tempo ativo em aplicativos classificados como improdutivos acima da régua{scope}",
            "person_long_days" => $"{group.Count} sinal(is) de EQUILÍBRIO por pessoa: dias de máquina ligada acima da régua (máquina ligada inclui ocioso e bloqueado)",
            "person_after_hours" => $"{group.Count} sinal(is) de EQUILÍBRIO por pessoa: atividade fora do horário de trabalho declarado",
            "goal_at_risk" => "Meta de horas ativas da semana projetada abaixo do combinado (projeção não é resultado)",
            "business_day_no_data" => $"{group.Count} dispositivo(s) sem dado em dia útil",
            _ => $"{group.Count} alerta(s) de gestão do tipo {group.Kind}",
        };
    }

    private static string Plural(AlertGroup group) =>
        group.Count > 1 ? $" em {group.Count} equipe(s)" : string.Empty;

    /// <summary>Data curta pt-BR: 02/03.</summary>
    public static string Short(DateOnly date) => date.ToString("dd/MM", PtBr);

    /// <summary>Data longa pt-BR: 02/03/2026.</summary>
    public static string Long(DateOnly date) => date.ToString("dd/MM/yyyy", PtBr);
}

namespace M351.Infrastructure.Analytics;

/// <summary>
/// DECOMPOSIÇÃO EXATA DA VARIAÇÃO DO ÍNDICE (item 1 do estudo, "índice explicável").
///
/// O índice de produtividade é P/C — produtivo sobre classificado (decisão 4 do spec de
/// 07/09/2026). A variação entre dois períodos admite uma identidade algébrica que reparte a
/// mudança entre os membros SEM RESÍDUO:
///
///     P₁/C₁ − P₀/C₀ = (P₁C₀ − P₀C₁)/(C₁C₀) = (C₀·ΔP − P₀·ΔC)/(C₁C₀)
///                    = Σₖ (C₀·Δpₖ − P₀·Δcₖ)/(C₁C₀)
///
/// porque ΔP = Σₖ Δpₖ e ΔC = Σₖ Δcₖ. Cada membro k — um aplicativo, uma equipe, um dia — leva
/// exatamente a sua parcela, e a soma das parcelas É a variação total.
///
/// POR QUE ISSO IMPORTA: sem a identidade sobraria uma fatia "outros" para fechar a conta, e a
/// frase que o produto quer dizer ("menos 4 pontos: mais 6 h em WhatsApp na equipe Comercial na
/// terça") seria um chute de atribuição vestido de número. Aqui ela é verificável — o teste da
/// soma é o contrato, e está em IndexDecompositionTests.
///
/// A PARTE NÃO ÓBVIA: a parcela NÃO é "o índice deste app". Um aplicativo improdutivo que cresceu
/// derruba o índice mesmo sem um único segundo produtivo, porque engorda C sem engordar P. Quem
/// captura isso é o segundo termo, −P₀·Δcₖ. Por isso a decomposição fala em CONTRIBUIÇÃO para a
/// variação, nunca em desempenho do membro.
///
/// Fora daqui: nada de SQL e nada de HTTP. Quem lê o Postgres é o IndexExplainedController, que
/// monta os MemberSeconds e chama estes dois métodos.
/// </summary>
public static class IndexDecomposition
{
    /// <summary>
    /// Parcelas em PONTOS do índice (escala 0–100), com sinal — negativo puxou para baixo.
    ///
    /// null quando falta denominador em qualquer um dos dois períodos: sem índice anterior não há
    /// variação a explicar, e devolver zero afirmaria "não mudou nada" onde a verdade é "não dá
    /// para saber". É a mesma régua de "sem denominador devolve null, nunca zero" que o
    /// DashboardController aplica ao índice e à cobertura.
    /// </summary>
    public static IReadOnlyList<MemberContribution>? Decompose(IReadOnlyList<MemberSeconds> members)
    {
        if (!TryTotals(members, out var p0, out var c0, out var c1))
        {
            return null;
        }

        // C₁·C₀ em double: o produto de dois totais de segundos estoura long num período longo
        // de frota grande (92 dias × milhares de lanes), e o resultado é fracionário de todo jeito
        var denominator = (double)c1 * c0;

        return members.Select(m => new MemberContribution(
            m.Key,
            m.Label,
            Points: (c0 * (double)(m.WorkCurrent - m.WorkPrevious)
                     - p0 * (double)(m.ClassifiedCurrent - m.ClassifiedPrevious)) / denominator * 100d,
            m.WorkCurrent,
            m.WorkPrevious,
            m.ClassifiedCurrent,
            m.ClassifiedPrevious)).ToList();
    }

    /// <summary>
    /// Variação total em pontos. Calculada dos TOTAIS, não da soma das parcelas: assim o teste da
    /// soma compara dois caminhos independentes e realmente prova a identidade.
    /// </summary>
    public static double? DeltaPoints(IReadOnlyList<MemberSeconds> members)
    {
        if (!TryTotals(members, out var p0, out var c0, out var c1))
        {
            return null;
        }

        var p1 = members.Sum(m => m.WorkCurrent);
        return ((double)p1 / c1 - (double)p0 / c0) * 100d;
    }

    /// <summary>
    /// Totais dos dois períodos. false quando qualquer um dos classificados é zero — inclusive
    /// para a lista vazia, que cai aqui sozinha (soma de nada é zero).
    /// </summary>
    private static bool TryTotals(IReadOnlyList<MemberSeconds> members, out long p0, out long c0, out long c1)
    {
        p0 = members.Sum(m => m.WorkPrevious);
        c0 = members.Sum(m => m.ClassifiedPrevious);
        c1 = members.Sum(m => m.ClassifiedCurrent);
        return c0 > 0 && c1 > 0;
    }
}

/// <summary>
/// Os segundos de um membro (aplicativo, equipe ou dia) nos dois períodos.
/// <paramref name="Key"/> é a chave estável para a tela navegar (process_name, nome da equipe,
/// data); <paramref name="Label"/> é o que se mostra a gente.
/// "Classified" é produtivo + neutro + improdutivo — o denominador da fórmula. Tempo SEM
/// classificação fica de fora dos dois lados, porque também está fora do índice.
/// </summary>
public readonly record struct MemberSeconds(
    string Key,
    string Label,
    long WorkCurrent,
    long WorkPrevious,
    long ClassifiedCurrent,
    long ClassifiedPrevious);

/// <summary>
/// A contribuição do membro para a variação do índice, em pontos e com sinal, carregando os
/// segundos que a originaram — a tela precisa deles para dizer "mais 6 h" ao lado de "−4 pontos"
/// sem uma segunda requisição.
/// </summary>
public readonly record struct MemberContribution(
    string Key,
    string Label,
    double Points,
    long WorkCurrent,
    long WorkPrevious,
    long ClassifiedCurrent,
    long ClassifiedPrevious);

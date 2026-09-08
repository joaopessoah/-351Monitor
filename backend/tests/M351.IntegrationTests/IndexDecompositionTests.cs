using M351.Infrastructure.Analytics;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// A decomposição da variação do índice (item 1 do estudo, "índice explicável").
///
/// O teste que carrega o contrato é o da SOMA: as parcelas por membro têm de fechar a variação
/// total, exatamente. Se sobrasse resíduo, o cartão "por que o índice mudou" precisaria de uma
/// fatia "outros" para não mentir — e a frase "menos 4 pontos porque subiu o WhatsApp" viraria
/// chute de atribuição.
///
/// Sem banco: é matemática pura, e o serviço que a usa é quem fala com o Postgres.
/// </summary>
public class IndexDecompositionTests
{
    /// <summary>Membro pelos quatro números que importam: produtivo e classificado, agora e antes.</summary>
    private static MemberSeconds M(string key, long workNow, long workBefore, long classifiedNow, long classifiedBefore) =>
        new(key, key, workNow, workBefore, classifiedNow, classifiedBefore);

    [Fact]
    public void Parcelas_Somam_Exatamente_A_Variacao_Total()
    {
        // um app improdutivo cresce 5 h, um produtivo cresce 1 h, um terceiro fica parado
        var membros = new[]
        {
            M("zap", 0, 0, 21_600, 3_600),
            M("erp", 28_800, 25_200, 28_800, 25_200),
            M("mail", 3_600, 3_600, 3_600, 3_600),
        };

        var parcelas = IndexDecomposition.Decompose(membros);
        var total = IndexDecomposition.DeltaPoints(membros);

        Assert.NotNull(parcelas);
        Assert.NotNull(total);
        Assert.Equal(total!.Value, parcelas!.Sum(p => p.Points), precision: 9);
    }

    [Fact]
    public void Soma_Fecha_Tambem_Quando_O_Indice_Sobe()
    {
        // espelho do caso anterior: agora o improdutivo encolhe e o produtivo cresce
        var membros = new[]
        {
            M("zap", 0, 0, 3_600, 21_600),
            M("erp", 28_800, 21_600, 28_800, 21_600),
        };

        var parcelas = IndexDecomposition.Decompose(membros);
        var total = IndexDecomposition.DeltaPoints(membros);

        Assert.True(total > 0, "o índice subiu no período");
        Assert.Equal(total!.Value, parcelas!.Sum(p => p.Points), precision: 9);
    }

    [Fact]
    public void Sinal_Da_Parcela_Aponta_Quem_Puxou_Para_Baixo()
    {
        var membros = new[]
        {
            M("zap", 0, 0, 21_600, 3_600),
            M("erp", 28_800, 25_200, 28_800, 25_200),
        };

        var parcelas = IndexDecomposition.Decompose(membros)!;

        Assert.True(parcelas.Single(p => p.Key == "zap").Points < 0, "o improdutivo que cresceu puxa para baixo");
        Assert.True(parcelas.Single(p => p.Key == "erp").Points > 0, "o produtivo que cresceu puxa para cima");
    }

    [Fact]
    public void App_Improdutivo_Que_Cresce_Derruba_O_Indice_Sem_Nenhum_Segundo_Produtivo()
    {
        // a parte que não é óbvia da fórmula: o segundo termo (−P₀·Δcₖ). O zap não tem um único
        // segundo produtivo nos dois períodos, e mesmo assim é o responsável pela queda, porque
        // engorda o denominador sem engordar o numerador.
        var membros = new[]
        {
            M("zap", 0, 0, 21_600, 3_600),
            M("erp", 25_200, 25_200, 25_200, 25_200),
        };

        var parcelas = IndexDecomposition.Decompose(membros)!;

        Assert.Equal(0, parcelas.Single(p => p.Key == "erp").Points, precision: 9);
        Assert.Equal(IndexDecomposition.DeltaPoints(membros)!.Value,
            parcelas.Single(p => p.Key == "zap").Points, precision: 9);
    }

    [Fact]
    public void Sem_Denominador_No_Periodo_Anterior_Devolve_Null_E_Nunca_Zero()
    {
        // período anterior vazio: não existe índice anterior, logo não existe variação a
        // explicar. Zero afirmaria "não mudou nada" onde a verdade é "não dá para saber".
        var membros = new[] { M("erp", 3_600, 0, 3_600, 0) };

        Assert.Null(IndexDecomposition.Decompose(membros));
        Assert.Null(IndexDecomposition.DeltaPoints(membros));
    }

    [Fact]
    public void Sem_Denominador_No_Periodo_Atual_Devolve_Null()
    {
        var membros = new[] { M("erp", 0, 3_600, 0, 3_600) };

        Assert.Null(IndexDecomposition.Decompose(membros));
        Assert.Null(IndexDecomposition.DeltaPoints(membros));
    }

    [Fact]
    public void Lista_Vazia_Devolve_Null()
    {
        Assert.Null(IndexDecomposition.Decompose([]));
        Assert.Null(IndexDecomposition.DeltaPoints([]));
    }

    [Fact]
    public void Periodo_Sem_Mudanca_Nenhuma_Da_Zero_Pontos()
    {
        var membros = new[] { M("erp", 25_200, 25_200, 28_800, 28_800) };

        Assert.Equal(0d, IndexDecomposition.DeltaPoints(membros)!.Value, precision: 9);
        Assert.All(IndexDecomposition.Decompose(membros)!, p => Assert.Equal(0d, p.Points, precision: 9));
    }

    [Fact]
    public void Parcela_Preserva_Os_Segundos_Para_A_Tela_Dizer_Quantas_Horas()
    {
        // a frase alvo é "menos 4 pontos: mais 6 h em WhatsApp" — os pontos vêm da fórmula, as
        // horas vêm daqui. Sem os segundos na parcela a tela teria de buscar de novo.
        var parcelas = IndexDecomposition.Decompose([M("zap", 0, 0, 21_600, 3_600), M("erp", 25_200, 25_200, 25_200, 25_200)])!;

        var zap = parcelas.Single(p => p.Key == "zap");
        Assert.Equal(21_600, zap.ClassifiedCurrent);
        Assert.Equal(3_600, zap.ClassifiedPrevious);
        Assert.Equal(0, zap.WorkCurrent);
        Assert.Equal(0, zap.WorkPrevious);
    }
}

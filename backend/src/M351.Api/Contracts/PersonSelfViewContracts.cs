namespace M351.Api.Contracts;

/// <summary>
/// A VISÃO DO COLABORADOR (item 2 do estudo, decisão 10 do spec de 07/09/2026): os mesmos números
/// que a pessoa veria sobre si, servidos DENTRO do painel para quem já tem acesso.
///
/// O colaborador não é usuário do sistema. Não existe login, token pessoal nem rota pública com
/// dado dele — este contrato é lido por Viewer+ autenticado, e o que chega às mãos da pessoa
/// chega pelo resumo em PDF que o gestor imprime ou pelo digest pessoal por e-mail.
///
/// ÍNDICE E COBERTURA VÊM DAQUI, calculados no servidor pela fórmula única da decisão 4. Antes
/// deste contrato a página da pessoa refazia a conta no cliente, o que eram duas fórmulas para
/// manter em sincronia — e a razão de a duplicação estar marcada como pendência no ponto de
/// retomada. Ambos null quando falta denominador; nunca zero, que leria como "índice péssimo" em
/// vez de "sem dado".
/// </summary>
public sealed record PersonSelfViewResponse(
    string WindowsSid,
    string? DisplayName,
    string? TeamName,
    OverviewPeriodResponse Period,
    long SecondsOn,
    long SecondsActive,
    long SecondsIdle,
    long SecondsLocked,
    long SecondsWorkRelated,
    long SecondsNeutral,
    long SecondsNotWorkRelated,
    long SecondsUnclassified,
    double? ProductivityIndex,
    double? ClassificationCoverage,
    int DaysWithData,
    int DeviceCount,
    IReadOnlyList<PersonSelfViewAppRow> TopApps,
    int OpenNotes,
    int OpenDisputes);

/// <summary>
/// Um aplicativo da pessoa no período.
///
/// <paramref name="Classification"/> é +1/0/−1 quando a empresa classificou, e NULL quando
/// ninguém classificou. Null não é zero: zero significa "a empresa decidiu que isto é neutro", e
/// dizer neutro onde não houve decisão seria inventar uma posição da empresa sobre o trabalho da
/// pessoa — exatamente o tipo de afirmação que a tela do colaborador não pode fazer.
/// </summary>
public sealed record PersonSelfViewAppRow(
    string ProcessName,
    string DisplayName,
    int? Classification,
    long SecondsActive);

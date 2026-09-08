using M351.Infrastructure.Digest;
using Npgsql;

namespace M351.Infrastructure.Exports;

/// <summary>
/// Geração do kind <c>resumo_pdf</c> (F6, diferencial 4 do estudo): o resumo semanal do
/// gestor em PDF, no MESMO desenho assíncrono dos CSVs — fila export_jobs, claim com SKIP
/// LOCKED, prazo de validade de 7 dias, download autenticado e trilha <c>export_csv</c>
/// gravada pelo POST /exports.
///
/// Os números vêm de <see cref="WeeklySummary"/>, a mesma fonte do digest do gestor por
/// e-mail: comparar o e-mail com o PDF tem de dar o mesmo número, então não existe SQL
/// próprio aqui. O rodapé de toda página traz o disclaimer da Portaria 671 VERBATIM
/// (<see cref="ExportService.JornadaDisclaimer"/>), igual ao CSV de jornada.
///
/// O PDF é AGREGADO: nenhuma lista de pessoas, nenhuma ordenação por desempenho.
/// </summary>
public sealed partial class ExportService
{
    /// <summary>
    /// Renderiza o PDF do resumo do período pedido (from/to dos params, datas locais do
    /// tenant). A "semana anterior" da comparação é a janela imediatamente anterior, do
    /// MESMO tamanho — assim o delta em pontos faz sentido também num recorte de 14 dias.
    /// row_count = linhas de dados variáveis do documento (apps + grupos de alerta).
    /// </summary>
    private async Task<(int Rows, bool Truncated)> GenerateResumoPdfAsync(
        ExportJobRow job, string absolutePath, CancellationToken ct)
    {
        var p = ParseParams(job.ParamsJson);
        var from = DateOnly.Parse(p.From);
        var to = DateOnly.Parse(p.To);

        // janela anterior do mesmo tamanho, encostada no início do período
        var span = to.DayNumber - from.DayNumber + 1;
        var previousTo = from.AddDays(-1);
        var previousFrom = previousTo.AddDays(-(span - 1));

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // tenant_id manuscrito: o PDF é do tenant do job e de mais ninguém
        await using var orgCommand = new NpgsqlCommand(
            "SELECT name, classification_vocabulary FROM organizations WHERE id = @t", connection);
        orgCommand.Parameters.AddWithValue("t", job.TenantId);

        string organizationName;
        string vocabulary;
        await using (var reader = await orgCommand.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"Organização {job.TenantId} não encontrada.");
            organizationName = reader.GetString(0);
            vocabulary = reader.GetString(1);
        }

        // VARIANTE PESSOAL (F9, decisão 10): params.windows_sid restringe o resumo a UMA
        // pessoa, para o gestor imprimir e entregar no 1:1. É o caminho de PAPEL — não existe
        // acesso ao painel para o colaborador, e este PDF é gerado por quem já tem o acesso.
        // Os números saem das MESMAS funções do digest pessoal por e-mail: comparar o e-mail
        // com o papel tem de dar o mesmo número.
        ManagerSummary summary;
        string? personLabel = null;

        if (!string.IsNullOrWhiteSpace(p.WindowsSid))
        {
            var atual = await PersonTotalsAsync(connection, job.TenantId, p.WindowsSid, from, to, ct);
            var anterior = await PersonTotalsAsync(connection, job.TenantId, p.WindowsSid, previousFrom, previousTo, ct);
            personLabel = atual.Label;
            summary = new ManagerSummary(
                atual.Totals,
                anterior.Totals,
                await WeeklySummary.PersonTopAppsAsync(connection, job.TenantId, p.WindowsSid, from, to, ct),
                // alerta de gestão fica FORA do papel da pessoa: é insumo de quem gere
                []);
        }
        else
        {
            summary = new ManagerSummary(
                await WeeklySummary.TotalsAsync(connection, job.TenantId, from, to, ct),
                await WeeklySummary.TotalsAsync(connection, job.TenantId, previousFrom, previousTo, ct),
                await WeeklySummary.TopAppsAsync(connection, job.TenantId, from, to, ct),
                await WeeklySummary.LiveAlertsAsync(connection, job.TenantId, ct));
        }

        await using var stream = new FileStream(absolutePath, FileMode.Create, FileAccess.Write, FileShare.None);
        var rows = ResumoPdfRenderer.Render(
            stream, organizationName, vocabulary, from, to, summary, JornadaDisclaimer, personLabel);

        // o PDF é um sumário: não existe teto de linhas a estourar, então nunca é truncado
        return (rows, false);
    }

    /// <summary>
    /// Totais de UMA pessoa no período, com o rótulo que a identifica no documento.
    ///
    /// Reusa WeeklySummary.PeopleTotalsAsync — a mesma fonte do digest pessoal por e-mail —
    /// e filtra pelo SID canônico. Pessoa sem NENHUM dado no período não sai da consulta
    /// (HAVING seconds_on > 0), e nesse caso o resumo vem zerado com o rótulo do SID: um PDF
    /// honesto de "não houve atividade" é melhor do que uma falha, porque período sem dado é
    /// uma resposta legítima (férias, afastamento, máquina em manutenção).
    /// </summary>
    private static async Task<(WeekTotals Totals, string Label)> PersonTotalsAsync(
        NpgsqlConnection connection, Guid tenantId, string personSid,
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        var people = await WeeklySummary.PeopleTotalsAsync(connection, tenantId, from, to, ct);
        var person = people.FirstOrDefault(x => x.Sid == personSid);
        return person is null
            ? (new WeekTotals(0, 0, 0, 0, 0, 0, 0, 0, 0, 1), personSid)
            : (person.Totals, person.Label);
    }
}

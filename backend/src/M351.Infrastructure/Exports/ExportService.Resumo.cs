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

        var summary = new ManagerSummary(
            await WeeklySummary.TotalsAsync(connection, job.TenantId, from, to, ct),
            await WeeklySummary.TotalsAsync(connection, job.TenantId, previousFrom, previousTo, ct),
            await WeeklySummary.TopAppsAsync(connection, job.TenantId, from, to, ct),
            await WeeklySummary.LiveAlertsAsync(connection, job.TenantId, ct));

        await using var stream = new FileStream(absolutePath, FileMode.Create, FileAccess.Write, FileShare.None);
        var rows = ResumoPdfRenderer.Render(
            stream, organizationName, vocabulary, from, to, summary, JornadaDisclaimer);

        // o PDF é um sumário: não existe teto de linhas a estourar, então nunca é truncado
        return (rows, false);
    }
}

using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace M351.Infrastructure.Digest;

/// <summary>
/// PDF do resumo semanal (F6, kind de export <c>resumo_pdf</c>): os MESMOS números do
/// digest do gestor, no mesmo desenho assíncrono dos CSVs (fila export_jobs, prazo de
/// validade, download autenticado, trilha export_csv). Quem compara o e-mail com o PDF
/// tem de ver número idêntico — por isso os dois leem de WeeklySummary, nunca de SQL
/// próprio.
///
/// REGRAS INEGOCIÁVEIS, iguais às do e-mail:
///  - AGREGADO. Nenhuma lista de pessoas, nenhuma ordenação por desempenho, em nenhuma
///    página. Alerta de escopo pessoa entra como contagem, sem nome;
///  - índice SEMPRE com a cobertura ao lado (decisão 4 do spec);
///  - ocioso jamais rotulado como improdutivo;
///  - o disclaimer da Portaria 671 no RODAPÉ DE TODA PÁGINA, verbatim do CSV de jornada
///    (ExportService.JornadaDisclaimer) — é o mesmo texto do banner do portal.
///
/// LICENÇA: QuestPDF Community (gratuita abaixo de US$ 1 M de faturamento anual) é
/// declarada em código. O worker declara no startup (Program.cs); o inicializador estático
/// aqui garante o mesmo em qualquer host que renderize sem passar pelo startup do worker
/// (testes de integração, por exemplo). Atribuir a licença é idempotente.
/// </summary>
public static class ResumoPdfRenderer
{
    static ResumoPdfRenderer() => ConfigureLicense();

    /// <summary>Declara a licença Community. Idempotente; chamada também no startup do worker.</summary>
    public static void ConfigureLicense() => QuestPDF.Settings.License = LicenseType.Community;

    private const string Ink = "#1a2233";
    private const string Muted = "#5a6478";
    private const string Line = "#e4e8ef";
    private const string Wash = "#f4f7f0";

    /// <summary>
    /// Escreve o PDF no stream. Devolve quantas LINHAS DE DADOS variáveis o documento traz
    /// (aplicativos listados + grupos de alerta vivos) — é o número que vai para
    /// export_jobs.row_count, com a mesma semântica dos CSVs: o volume do que foi apurado.
    /// Os blocos fixos (três métricas, quatro baldes) não contam, porque existem sempre.
    /// </summary>
    public static int Render(
        Stream output,
        string organizationName,
        string? vocabulary,
        DateOnly weekStart,
        DateOnly weekEnd,
        ManagerSummary summary,
        string disclaimer)
    {
        var (workLabel, neutralLabel, notWorkLabel, unclassifiedLabel) = WeeklySummary.Labels(vocabulary);
        var totals = summary.Week;
        var previous = summary.Previous;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(style => style.FontSize(10).FontColor(Ink));

                page.Header().Column(header =>
                {
                    header.Item().Text("Resumo da semana").FontSize(18).SemiBold();
                    header.Item().Text($"{organizationName} · {DigestText.Long(weekStart)} a {DigestText.Long(weekEnd)}")
                        .FontSize(10).FontColor(Muted);
                    header.Item().Text(
                        $"{totals.PersonCount} pessoa(s) com dado · {totals.DeviceCount} dispositivo(s) · "
                        + "relatório agregado, sem comparação entre pessoas")
                        .FontSize(9).FontColor(Muted);
                    header.Item().PaddingTop(8).LineHorizontal(1).LineColor(Line);
                });

                page.Content().PaddingVertical(12).Column(content =>
                {
                    content.Spacing(14);

                    // ---------------------------------------- índice + cobertura, sempre juntos
                    content.Item().Background(Wash).Padding(12).Column(box =>
                    {
                        box.Spacing(4);
                        box.Item().Text($"Índice de produtividade: {DigestText.Pct(totals.Index)}")
                            .FontSize(14).SemiBold();
                        box.Item().Text(DigestText.IndexDelta(totals.Index, previous.Index))
                            .FontSize(10).FontColor(Muted);
                        box.Item().Text($"Cobertura da classificação: {DigestText.Pct(totals.Coverage)}")
                            .FontSize(12).SemiBold();
                        box.Item().Text(
                            $"Índice = {workLabel.ToLowerInvariant()} ÷ tempo ativo já classificado; "
                            + "o tempo em aplicativos sem categoria fica fora da conta e aparece na cobertura. "
                            + $"{WeeklySummary.Framing}, sobre aplicativos, nunca sobre pessoas.")
                            .FontSize(9).FontColor(Muted);
                    });

                    // ---------------------------------------- horas da semana
                    content.Item().Text("Horas da semana").FontSize(13).SemiBold();
                    content.Item().Table(table =>
                    {
                        Columns(table, 3);
                        Row(table, "Horas ativas da equipe", DigestText.Hours(totals.SecondsActive),
                            DigestText.HoursDelta(totals.SecondsActive, previous.SecondsActive));
                        Row(table, "Horas com a máquina ligada", DigestText.Hours(totals.SecondsOn),
                            DigestText.HoursDelta(totals.SecondsOn, previous.SecondsOn));
                        Row(table, "Horas ociosas", DigestText.Hours(totals.SecondsIdle),
                            DigestText.HoursDelta(totals.SecondsIdle, previous.SecondsIdle));
                    });
                    content.Item().Text(WeeklySummary.IdleLesson).FontSize(9).FontColor(Muted);

                    // ---------------------------------------- composição nos quatro baldes
                    content.Item().Text("Composição do tempo ativo").FontSize(13).SemiBold();
                    content.Item().Table(table =>
                    {
                        Columns(table, 3);
                        Bucket(table, workLabel, totals.SecondsWork, totals.SecondsActive);
                        Bucket(table, neutralLabel, totals.SecondsNeutral, totals.SecondsActive);
                        Bucket(table, notWorkLabel, totals.SecondsNotWork, totals.SecondsActive);
                        Bucket(table, unclassifiedLabel, totals.SecondsUnclassified, totals.SecondsActive);
                    });

                    if (summary.Alerts.Count > 0)
                    {
                        content.Item().Text("Alertas de gestão em aberto").FontSize(13).SemiBold();
                        content.Item().Column(list =>
                        {
                            list.Spacing(3);
                            foreach (var group in summary.Alerts)
                            {
                                list.Item().Text($"• {DigestText.Alert(group)}").FontSize(10);
                            }
                        });
                    }

                    if (summary.TopApps.Count > 0)
                    {
                        content.Item().Text("Aplicativos mais usados").FontSize(13).SemiBold();
                        content.Item().Table(table =>
                        {
                            Columns(table, 3);
                            foreach (var app in summary.TopApps)
                            {
                                Row(table, app.DisplayName, DigestText.Hours(app.SecondsActive),
                                    WeeklySummary.AppLabel(app.Classification, vocabulary));
                            }
                        });
                    }
                });

                // rodapé de TODA página: o disclaimer da Portaria 671, verbatim
                page.Footer().Column(footer =>
                {
                    footer.Item().PaddingBottom(6).LineHorizontal(1).LineColor(Line);
                    footer.Item().Text(disclaimer).FontSize(8).FontColor(Muted);
                    footer.Item().Text(text =>
                    {
                        text.DefaultTextStyle(style => style.FontSize(8).FontColor(Muted));
                        text.Span("Relatório agregado: o +351 Monitor não ordena pessoas por desempenho. Página ");
                        text.CurrentPageNumber();
                        text.Span(" de ");
                        text.TotalPages();
                    });
                });
            });
        });

        document.GeneratePdf(output);
        return summary.TopApps.Count + summary.Alerts.Count;
    }

    private static void Columns(TableDescriptor table, int count) =>
        table.ColumnsDefinition(columns =>
        {
            columns.RelativeColumn(3);
            for (var i = 1; i < count; i++)
            {
                columns.RelativeColumn(2);
            }
        });

    private static void Row(TableDescriptor table, string label, string value, string note)
    {
        table.Cell().BorderBottom(1).BorderColor(Line).PaddingVertical(4).Text(label).FontSize(10);
        table.Cell().BorderBottom(1).BorderColor(Line).PaddingVertical(4)
            .AlignRight().Text(value).FontSize(10).SemiBold();
        table.Cell().BorderBottom(1).BorderColor(Line).PaddingVertical(4)
            .AlignRight().Text(note).FontSize(9).FontColor(Muted);
    }

    private static void Bucket(TableDescriptor table, string label, long seconds, long activeSeconds) =>
        Row(table, label, DigestText.Hours(seconds), DigestText.Share(seconds, activeSeconds));
}

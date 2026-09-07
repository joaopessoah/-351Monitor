namespace M351.Infrastructure.Capacity;

/// <summary>
/// F7 — o DENOMINADOR da capacidade utilizada, em um lugar só.
///
/// Capacidade utilizada = <c>ativo ÷ (jornada declarada × dias úteis × pessoas)</c>
/// (spec seção 2.2). Esta classe resolve a parte que não depende de quem trabalhou:
/// quantas horas tem a jornada declarada e quantos dias úteis tem o período — já SEM os
/// feriados, que é a correção que a F7 traz (até aqui o cartão de equipes da Visão Geral
/// contava o feriado como dia útil e mostrava capacidade subestimada).
///
/// A regra mora no SERVIDOR de propósito: o portal já derivava jornada e dias úteis do
/// business_hours por conta própria, e agora existem DUAS jornadas possíveis (a da equipe e a
/// da organização) mais o calendário de feriados. Duplicar isso no cliente garantiria que as
/// duas contas divergissem na primeira mudança.
/// </summary>
public static class CapacityBase
{
    /// <summary>Jornada padrão quando nem a equipe nem a organização declararam uma (8 h).</summary>
    public const double DefaultDailyHours = 8;

    /// <summary>Dias úteis padrão (segunda a sexta, ISO 1..5) quando não há jornada declarada.</summary>
    public static readonly int[] DefaultIsoDays = [1, 2, 3, 4, 5];

    /// <summary>
    /// Base de capacidade de UM período: horas por dia da jornada declarada, dias úteis do
    /// período já descontados os feriados, e quantos feriados caíram em dia útil (o número que
    /// a interface mostra para explicar por que o denominador encolheu).
    /// </summary>
    public readonly record struct Result(double DailyHours, int BusinessDays, int HolidaysExcluded)
    {
        /// <summary>Segundos de capacidade de UMA pessoa no período (multiplique por pessoas).</summary>
        public long SecondsPerPerson => (long)Math.Round(DailyHours * 3600 * BusinessDays);
    }

    /// <summary>
    /// Calcula a base. <paramref name="workHoursJson"/> é o jsonb da jornada JÁ resolvido pela
    /// precedência equipe → organização (null nos dois casos cai no padrão 8 h, seg–sex).
    /// <paramref name="holidays"/> são as datas de feriado da organização; feriado em fim de
    /// semana (ou em dia que a jornada não cobre) não desconta nada, porque não era dia útil.
    /// </summary>
    public static Result Compute(
        string? workHoursJson, DateOnly from, DateOnly to, IReadOnlySet<DateOnly> holidays)
    {
        var isoDays = DefaultIsoDays;
        var dailyHours = DefaultDailyHours;

        if (BusinessHoursWindow.TryParse(workHoursJson, out var schedule) && schedule is not null)
        {
            // dias vazios na jornada declarada = configuração sem sentido para capacidade;
            // mantém o padrão seg–sex em vez de devolver denominador zero
            if (schedule.IsoDays.Length > 0) isoDays = schedule.IsoDays;
            var hours = (schedule.End - schedule.Start).TotalHours;
            if (hours > 0) dailyHours = hours;
        }

        var businessDays = 0;
        var holidaysExcluded = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var iso = day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;
            if (!isoDays.Contains(iso)) continue; // fim de semana / dia fora da jornada
            if (holidays.Contains(day))
            {
                holidaysExcluded++; // F7: o dia não existiu — sai do denominador
                continue;
            }

            businessDays++;
        }

        return new Result(dailyHours, businessDays, holidaysExcluded);
    }
}

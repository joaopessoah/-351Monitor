using Npgsql;

namespace M351.Infrastructure.Capacity;

/// <summary>
/// F7 — feriados NACIONAIS brasileiros e a semeadura de <c>organization_holidays</c>.
///
/// Para que servem: a métrica "capacidade utilizada" é
/// <c>ativo ÷ (jornada declarada × dias úteis × pessoas)</c>. Sem feriados, um mês com dois
/// feriados conta dois dias que não existiram no denominador e a capacidade sai SUBESTIMADA —
/// exatamente o número que o site usa para responder "preciso contratar?". Feriado sai do
/// denominador; não muda agregado nenhum (por isso mudar feriado NÃO reagrega nada).
///
/// O QUE É SEMEADO: só o que é feriado nacional POR LEI (Lei 662/1949, Lei 10.607/2002,
/// Lei 6.802/1980, Lei 9.093/1995 para a Paixão de Cristo e Lei 14.759/2023 para o 20 de
/// novembro). Carnaval (segunda e terça) e Corpus Christi ficam DE FORA de propósito: são
/// ponto facultativo federal, não feriado — muita empresa trabalha. Semeá-los tiraria dias do
/// denominador de quem trabalhou e inflaria a capacidade, que é o erro oposto (e pior, porque
/// silencioso). A tela de Equipes lista os dois como sugestão de um clique para quem os observa.
///
/// Datas móveis saem da Páscoa (algoritmo gregoriano anônimo, Meeus/Jones/Butcher).
/// </summary>
public static class BrazilianHolidays
{
    /// <summary>Um feriado do calendário: dia e nome exibido.</summary>
    public readonly record struct Holiday(DateOnly Date, string Name);

    /// <summary>
    /// Feriados NACIONAIS de um ano, ordenados por data. Não inclui ponto facultativo
    /// (Carnaval, Corpus Christi) nem feriado estadual/municipal — ver o cabeçalho.
    /// </summary>
    public static IReadOnlyList<Holiday> ForYear(int year)
    {
        var easter = Easter(year);
        var holidays = new List<Holiday>
        {
            new(new DateOnly(year, 1, 1), "Confraternização Universal"),
            new(easter.AddDays(-2), "Sexta-feira Santa (Paixão de Cristo)"),
            new(new DateOnly(year, 4, 21), "Tiradentes"),
            new(new DateOnly(year, 5, 1), "Dia do Trabalho"),
            new(new DateOnly(year, 9, 7), "Independência do Brasil"),
            new(new DateOnly(year, 10, 12), "Nossa Senhora Aparecida"),
            new(new DateOnly(year, 11, 2), "Finados"),
            new(new DateOnly(year, 11, 15), "Proclamação da República"),
            new(new DateOnly(year, 11, 20), "Dia Nacional de Zumbi e da Consciência Negra"),
            new(new DateOnly(year, 12, 25), "Natal"),
        };
        holidays.Sort((a, b) => a.Date.CompareTo(b.Date));
        return holidays;
    }

    /// <summary>
    /// Ponto facultativo federal do ano (Carnaval e Corpus Christi). NÃO é semeado: fica
    /// disponível para a organização que os observa adicionar de uma vez pela tela de Equipes.
    /// </summary>
    public static IReadOnlyList<Holiday> OptionalForYear(int year)
    {
        var easter = Easter(year);
        return
        [
            new Holiday(easter.AddDays(-48), "Carnaval (segunda-feira) — ponto facultativo"),
            new Holiday(easter.AddDays(-47), "Carnaval (terça-feira) — ponto facultativo"),
            new Holiday(easter.AddDays(60), "Corpus Christi — ponto facultativo"),
        ];
    }

    /// <summary>
    /// Semeia os feriados nacionais dos anos pedidos para o tenant. IDEMPOTENTE
    /// (<c>ON CONFLICT DO NOTHING</c>): re-executar não sobrescreve o que o cliente editou nem
    /// ressuscita o feriado que ele apagou de propósito. Retorna quantas linhas entraram.
    /// </summary>
    public static async Task<int> SeedAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid tenantId,
        IEnumerable<int> years, CancellationToken ct = default)
    {
        var holidays = years.Distinct().SelectMany(ForYear).ToList();
        if (holidays.Count == 0) return 0;

        // um comando só com dois arrays paralelos (unnest): N feriados, uma ida ao banco
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO organization_holidays (tenant_id, holiday_date, name)
            SELECT @t, d, n FROM unnest(@dates::date[], @names::text[]) AS h(d, n)
            ON CONFLICT (tenant_id, holiday_date) DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("dates", holidays.Select(h => h.Date).ToArray());
        command.Parameters.AddWithValue("names", holidays.Select(h => h.Name).ToArray());
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Semeadura padrão da CRIAÇÃO DA ORGANIZAÇÃO: ano corrente e o próximo (spec F7). Dois
    /// anos porque o painel compara com o período anterior e porque o cliente que entra em
    /// dezembro precisa do calendário de janeiro sem ter de pedir.
    /// </summary>
    public static Task<int> SeedCurrentAndNextYearAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid tenantId,
        DateOnly today, CancellationToken ct = default) =>
        SeedAsync(connection, transaction, tenantId, [today.Year, today.Year + 1], ct);

    /// <summary>Domingo de Páscoa (algoritmo gregoriano anônimo).</summary>
    private static DateOnly Easter(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = ((19 * a) + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        var m = (a + (11 * h) + (22 * l)) / 451;
        var month = (h + l - (7 * m) + 114) / 31;
        var day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }
}

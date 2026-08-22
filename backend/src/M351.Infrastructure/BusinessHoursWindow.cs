using System.Text.Json;

namespace M351.Infrastructure;

/// <summary>
/// Horário de trabalho da organização (business_hours, o jsonb da org
/// {"days":[1..7 ISO],"start":"08:00","end":"18:00"}) em UM lugar só:
///  - <see cref="TryParse"/> devolve a janela já validada — usada pelo relatório de atividade
///    fora do horário de trabalho, que precisa dos dias e dos limites, não só de um sim/não;
///  - <see cref="IsWithin"/> responde "está em horário de trabalho AGORA?" no fuso da
///    organização, espelho SERVER-SIDE do deviceHealth.ts do portal (F5): decide o realce do
///    "sem comunicação" no health-summary e as quiet hours dos alertas de frota.
///
/// Sem configuração (ou malformada), IsWithin considera SEMPRE dentro, não suprime alerta.
/// </summary>
public static class BusinessHoursWindow
{
    /// <summary>
    /// Janela válida: dias ISO (1 = segunda ... 7 = domingo) e limites locais, com
    /// <c>Start</c> estritamente anterior a <c>End</c>.
    /// </summary>
    public sealed record Schedule(int[] IsoDays, TimeOnly Start, TimeOnly End);

    /// <summary>
    /// Interpreta o jsonb de business_hours. false = não configurado ou malformado (não há
    /// janela a aplicar); nesse caso <paramref name="schedule"/> sai null.
    /// </summary>
    public static bool TryParse(string? businessHoursJson, out Schedule? schedule)
    {
        schedule = null;
        if (string.IsNullOrWhiteSpace(businessHoursJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(businessHoursJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("days", out var daysEl) || daysEl.ValueKind != JsonValueKind.Array
                || !TryParseHm(root, "start", out var start) || !TryParseHm(root, "end", out var end)
                || end <= start)
            {
                return false;
            }

            var days = daysEl.EnumerateArray()
                .Where(d => d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var day) && day is >= 1 and <= 7)
                .Select(d => d.GetInt32())
                .Distinct()
                .Order()
                .ToArray();

            schedule = new Schedule(days, start, end);
            return true;
        }
        catch (JsonException)
        {
            return false; // configuração inválida é tratada como ausente
        }
    }

    public static bool IsWithin(string? businessHoursJson, string timezone, DateTimeOffset nowUtc)
    {
        if (!TryParse(businessHoursJson, out var schedule))
        {
            return true; // sem janela configurada: sempre dentro (nunca suprime alerta)
        }

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            var local = TimeZoneInfo.ConvertTime(nowUtc, tz);

            // dia ISO (1 = segunda ... 7 = domingo)
            var isoDay = local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek;
            if (!schedule!.IsoDays.Contains(isoDay))
            {
                return false;
            }

            var now = TimeOnly.FromDateTime(local.DateTime);
            return now >= schedule.Start && now < schedule.End;
        }
        catch (TimeZoneNotFoundException)
        {
            return true; // fuso inválido nunca suprime alerta
        }
    }

    private static bool TryParseHm(JsonElement root, string field, out TimeOnly time)
    {
        time = default;
        if (!root.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var parts = el.GetString()!.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var h) || h is < 0 or > 23
            || !int.TryParse(parts[1], out var m) || m is < 0 or > 59)
        {
            return false;
        }

        time = new TimeOnly(h, m);
        return true;
    }
}

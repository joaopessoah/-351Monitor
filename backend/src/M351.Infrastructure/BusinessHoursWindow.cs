using System.Text.Json;

namespace M351.Infrastructure;

/// <summary>
/// "Está em horário de trabalho AGORA?" no fuso da organização, espelho SERVER-SIDE do
/// deviceHealth.ts do portal (F5): decide o realce do "sem comunicação" no health-summary
/// e as quiet hours dos alertas de frota. business_hours é o jsonb da org
/// ({"days":[1..7 ISO],"start":"08:00","end":"18:00"}); sem configuração (ou malformada),
/// considera SEMPRE dentro, não suprime alerta.
/// </summary>
public static class BusinessHoursWindow
{
    public static bool IsWithin(string? businessHoursJson, string timezone, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(businessHoursJson))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(businessHoursJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("days", out var daysEl) || daysEl.ValueKind != JsonValueKind.Array
                || !TryParseHm(root, "start", out var start) || !TryParseHm(root, "end", out var end)
                || end <= start)
            {
                return true;
            }

            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            var local = TimeZoneInfo.ConvertTime(nowUtc, tz);

            // dia ISO (1 = segunda ... 7 = domingo)
            var isoDay = local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek;
            var dayMatches = daysEl.EnumerateArray().Any(d =>
                d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var day) && day == isoDay);
            if (!dayMatches)
            {
                return false;
            }

            var minutesNow = local.Hour * 60 + local.Minute;
            return minutesNow >= start && minutesNow < end;
        }
        catch (Exception ex) when (ex is JsonException or TimeZoneNotFoundException)
        {
            return true; // configuração inválida nunca suprime alerta
        }
    }

    private static bool TryParseHm(JsonElement root, string field, out int minutes)
    {
        minutes = 0;
        if (!root.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var parts = el.GetString()!.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m))
        {
            return false;
        }

        minutes = h * 60 + m;
        return true;
    }
}

using System.Globalization;
using M351.Agent.Core.Contracts;

namespace M351.Agent.Core.Collectors;

/// <summary>
/// collection_window (Seção 6.3): em BUSINESS_HOURS, fora de days/start/end o helper NÃO coleta
/// janela ativa nem idle (eventos de sessão/energia e heartbeat de máquina continuam no serviço).
/// </summary>
public static class ScheduleWindow
{
    public static bool IsCollectionAllowed(CollectionWindow? window, DateTime localNow)
    {
        if (window is null) return true;
        if (!string.Equals(window.Mode, CollectionWindowModes.BusinessHours, StringComparison.Ordinal)) return true;

        if (window.Days is { Count: > 0 })
        {
            var isoDay = localNow.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)localNow.DayOfWeek;
            if (!window.Days.Contains(isoDay)) return false;
        }

        if (TryParseTime(window.Start, out var start) && TryParseTime(window.End, out var end))
        {
            var t = TimeOnly.FromDateTime(localNow);
            if (t < start || t >= end) return false;
        }

        return true;
    }

    private static bool TryParseTime(string? value, out TimeOnly time)
    {
        time = default;
        return value is not null &&
               TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }
}

using System.Globalization;

namespace M351.Agent.Core;

/// <summary>Formatação ISO-8601 UTC canônica do contrato (ex.: 2026-06-09T14:32:07.512Z).</summary>
public static class Iso
{
    public const string Format8601 = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Format(DateTimeOffset utc) =>
        utc.UtcDateTime.ToString(Format8601, CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}

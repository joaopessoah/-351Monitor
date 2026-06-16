using System.Globalization;

namespace M351.Agent.Core.Update;

/// <summary>
/// Versao semantica MAJOR.MINOR.PATCH usada no auto-update (Secao 6.7). Comparacao NUMERICA
/// por componente — 1.10.0 &gt; 1.9.0 (o que uma comparacao de string quebraria). Pre-release e
/// build-metadata sao ignorados no MVP (canal unico estavel; sem beta/canary). Parsing tolerante:
/// componentes ausentes valem 0 (1.1 == 1.1.0), prefixo "v" aceito, espacos aparados.
/// </summary>
public readonly struct SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    public SemVer(int major, int minor, int patch)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    /// <summary>Faz o parse; retorna false sem lancar para entradas invalidas (ex.: manifesto corrompido).</summary>
    public static bool TryParse(string? value, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var core = value.Trim();
        if (core.StartsWith("v", StringComparison.OrdinalIgnoreCase)) core = core[1..];

        // Despreza pre-release ("-") e build-metadata ("+") no MVP.
        var dash = core.IndexOf('-');
        if (dash >= 0) core = core[..dash];
        var plus = core.IndexOf('+');
        if (plus >= 0) core = core[..plus];

        var parts = core.Split('.');
        if (parts.Length is 0 or > 3) return false;

        var nums = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0)
                return false;
            nums[i] = n;
        }

        version = new SemVer(nums[0], nums[1], nums[2]);
        return true;
    }

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        return Patch.CompareTo(other.Patch);
    }

    public bool Equals(SemVer other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVer v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch);

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator ==(SemVer a, SemVer b) => a.Equals(b);
    public static bool operator !=(SemVer a, SemVer b) => !a.Equals(b);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}

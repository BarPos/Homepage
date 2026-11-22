namespace Homepage.Shared;

/// <summary>
/// One version for the whole product. Directory.Build.props &lt;Version&gt; must stay the same; a test and
/// build/build-server.sh enforce it.
/// </summary>
public static class AppVersion
{
    public const string Current = "0.2.1";

    public static Version CurrentVersion { get; } = Normalize(Version.Parse(Current));

    /// <summary>Parses a plain <c>x.y.z</c> (or x.y / x.y.z.w) string, with an optional leading v. Pre-release suffixes are refused.</summary>
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        if (!Version.TryParse(text, out var parsed) || parsed is null) return false;
        version = Normalize(parsed);
        return true;
    }

    /// <summary>True for a plain x.y.z string (what update.sh also accepts).</summary>
    public static bool IsPlainTriplet(string? value) =>
        value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{1,5}\.\d{1,5}\.\d{1,5}$");

    public static int Compare(string a, string b)
    {
        if (!TryParse(a, out var va)) throw new FormatException($"Bad version '{a}'.");
        if (!TryParse(b, out var vb)) throw new FormatException($"Bad version '{b}'.");
        return va.CompareTo(vb);
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}

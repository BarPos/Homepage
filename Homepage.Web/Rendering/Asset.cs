namespace Homepage.Web.Rendering;

/// <summary>
/// Versioned static asset URLs, so browsers never keep an old script or stylesheet after an update or restart.
/// The token is the app version plus the process start time (assets only change with a new release or a restart).
/// </summary>
public static class Asset
{
    private static readonly string Token =
        $"{Homepage.Shared.AppVersion.Current}.{DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond % 1_000_000_000:x}";

    public static string Url(string path) => $"/_content/Homepage.Web/{path}?v={Token}";
}

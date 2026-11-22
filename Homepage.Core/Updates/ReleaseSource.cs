// Adapted from DomainNotice (MIT), https://github.com/BarPos/DomainNotice
namespace Homepage.Updates;

public sealed record ReleaseInfo(string Version, string TagName, string? Name, string? Notes, DateTime? PublishedAtUtc, bool Prerelease, string? HtmlUrl);

/// <summary>One downloadable file of a release.</summary>
public sealed record ReleaseAsset(string Name, long Size, string DownloadUrl);

/// <summary>A release with its assets.</summary>
public sealed record Release(ReleaseInfo Info, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? FindAsset(string name) => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}

/// <summary>Where releases come from. When <see cref="IsConfigured"/> is false, implementations make no network calls.</summary>
public interface IReleaseSource
{
    bool IsConfigured { get; }
    Task<Release?> GetLatestAsync(CancellationToken ct = default);
    Task<Stream> OpenAssetAsync(ReleaseAsset asset, CancellationToken ct = default);
}

public sealed class ReleaseSourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Release asset names (same scheme as build/build-server.sh and update.sh).</summary>
public static class ReleaseAssetNames
{
    public const string Sums = "SHA256SUMS.txt";
    public const string InstallScript = "install.sh";

    /// <summary><c>Homepage.Server-&lt;version&gt;-linux-&lt;arch&gt;.tar.gz</c>, arch <c>x64</c> or <c>arm64</c>.</summary>
    public static string ServerTarball(string version, string arch) => $"Homepage.Server-{version}-linux-{arch}.tar.gz";

    public static string Tag(string version) => "v" + version;
}

using System.Runtime.InteropServices;

// Adapted from DomainNotice (MIT), https://github.com/BarPos/DomainNotice
namespace Homepage.Updates;

/// <summary>What the running server knows about how it was installed. Injectable so tests run on any OS.</summary>
public interface IServerInstallation
{
    /// <summary>Release asset architecture: <c>x64</c>, <c>arm64</c>, or null when unsupported.</summary>
    string? Architecture { get; }
    bool SelfUpdateSupported { get; }
    /// <summary>Why self-update is not supported (shown in the Console), or null.</summary>
    string? UnsupportedReason { get; }
}

/// <summary>
/// Self-update needs: Linux on x64/arm64, Ubuntu 24.04 or newer, and the layout written by install.sh:
/// the process runs from <c>/opt/homepage/releases/&lt;version&gt;/</c>, <c>/opt/homepage/current</c> is a
/// symlink, and the <c>homepage-update.path</c> unit is installed.
/// </summary>
internal sealed class ServerInstallation : IServerInstallation
{
    public const string InstallRoot = "/opt/homepage";
    public const string UpdatePathUnit = "/etc/systemd/system/homepage-update.path";

    private readonly Lazy<(bool Supported, string? Reason)> _probe;

    public ServerInstallation()
    {
        Architecture = RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => null,
        };
        _probe = new Lazy<(bool, string?)>(Probe);
    }

    public string? Architecture { get; }
    public bool SelfUpdateSupported => _probe.Value.Supported;
    public string? UnsupportedReason => _probe.Value.Reason;

    private (bool, string?) Probe()
    {
        if (!OperatingSystem.IsLinux()) return (false, "Self-update works only on Ubuntu Linux (installed with install.sh).");
        if (Architecture is null) return (false, $"Unsupported CPU architecture {RuntimeInformation.OSArchitecture}.");
        if (!IsUbuntu2404OrNewer()) return (false, "Self-update needs Ubuntu 24.04 LTS or newer.");
        var releases = Path.Combine(InstallRoot, "releases") + Path.DirectorySeparatorChar;
        var currentDir = Path.Combine(InstallRoot, "current") + Path.DirectorySeparatorChar;
        var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
        // The apphost normally resolves the symlink (releases/<v>/), but accept current/ too.
        if (!baseDir.StartsWith(releases, StringComparison.Ordinal) && !baseDir.StartsWith(currentDir, StringComparison.Ordinal))
            return (false, $"The server does not run from {releases} (not installed with install.sh).");
        string? linkTarget;
        try { linkTarget = new FileInfo(Path.Combine(InstallRoot, "current")).LinkTarget; }
        catch (IOException) { linkTarget = null; }
        if (linkTarget is null)
            return (false, $"{InstallRoot}/current is not a symlink (not installed with install.sh).");
        if (!File.Exists(UpdatePathUnit)) return (false, "The homepage-update.path unit is not installed.");
        return (true, null);
    }

    private static bool IsUbuntu2404OrNewer()
    {
        try
        {
            if (!File.Exists("/etc/os-release")) return false;
            var values = UpdateRequestFile.ParseKeyValues(File.ReadAllText("/etc/os-release"));
            if (!values.TryGetValue("ID", out var id) || id.Trim('"') != "ubuntu") return false;
            return values.TryGetValue("VERSION_ID", out var v) && Version.TryParse(v.Trim('"'), out var version) &&
                   version >= new Version(24, 4);
        }
        catch (IOException)
        {
            return false;
        }
    }
}

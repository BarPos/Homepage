using System.Net;

namespace Homepage.Options;

/// <summary>Kestrel and reverse-proxy settings (section <c>Site</c>).</summary>
public sealed class SiteOptions
{
    public const string Section = "Site";
    /// <summary>Kestrel URLs, ';'-separated. install.sh writes http://127.0.0.1:5080 (behind a proxy) or https://0.0.0.0:443.</summary>
    public string Urls { get; set; } = "http://127.0.0.1:5080";
    public string Certificate { get; set; } = "";
    public string CertificateKey { get; set; } = "";
    public ForwardedHeadersSettings ForwardedHeaders { get; set; } = new();
}

public sealed class ForwardedHeadersSettings
{
    public bool Enabled { get; set; }
    public List<string> KnownProxies { get; set; } = [];
    public List<string> KnownNetworks { get; set; } = [];
}

/// <summary>Folders (section <c>Paths</c>). Only <see cref="Data"/> is writable for the service.</summary>
public sealed class PathsOptions
{
    public const string Section = "Paths";
    public string Data { get; set; } = "/opt/homepage/data";
    public string Backups { get; set; } = "/opt/homepage/backups";

    public string MediaDirectory => Path.Combine(Data, "media");
    public string DataProtectionDirectory => Path.Combine(Data, "keys", "dataprotection");
    public string UpdatesDirectory => Path.Combine(Data, "updates");
}

/// <summary>Self-update from GitHub releases (section <c>Updates</c>). A blank repository turns updates off.</summary>
public sealed class UpdatesOptions
{
    public const string Section = "Updates";
    public string GitHubRepository { get; set; } = "";
    public string Channel { get; set; } = "stable";
    public int CheckIntervalHours { get; set; } = 24;
    public string GitHubToken { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(GitHubRepository);
}

public static class ForwardedHeadersParsing
{
    public static IPAddress ParseProxy(string value) =>
        IPAddress.TryParse(value.Trim(), out var ip)
            ? ip
            : throw new InvalidOperationException($"Site:ForwardedHeaders:KnownProxies: '{value}' is not an IP address.");

    public static System.Net.IPNetwork ParseNetwork(string value) =>
        System.Net.IPNetwork.TryParse(value.Trim(), out var net)
            ? net
            : throw new InvalidOperationException($"Site:ForwardedHeaders:KnownNetworks: '{value}' is not a CIDR network.");
}

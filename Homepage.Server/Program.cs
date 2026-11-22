// Homepage.Server: the host process (Kestrel, background jobs, command line).
// Startup and command line adapted from DomainNotice (MIT), https://github.com/BarPos/DomainNotice
//
// Command line (install.sh and update.sh run these as the service user):
//   --migrate-only                  apply EF Core migrations, then block/theme content migrations, seed, and exit
//   --create-local-account <name>   create an admin account (admin: rw); password read from stdin (first line)
//   --reset-local-password <name>   set a new password and make the account valid; password read from stdin
//   --list-local-accounts           print "name<TAB>enabled|disabled" per account and exit
//
// Configuration: appsettings.json, appsettings.{Environment}.json, then the file named by HOMEPAGE_CONFIG
// (install.sh: /opt/homepage/config/appsettings.Production.json), then environment variables (HOMEPAGE_ prefix).

using System.Security.Cryptography.X509Certificates;
using Homepage;
using Homepage.Accounts;
using Homepage.Data;
using Homepage.Options;
using Homepage.Server;
using Homepage.Shared;
using Homepage.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

var migrateOnly = args.Contains("--migrate-only");
var createName = ArgValue(args, "--create-local-account");
var resetName = ArgValue(args, "--reset-local-password");
var listAccounts = args.Contains("--list-local-accounts");
if ((args.Contains("--create-local-account") && createName is null) || (args.Contains("--reset-local-password") && resetName is null))
{
    Console.Error.WriteLine("The option needs a user name.");
    return 2;
}
var cliOnly = migrateOnly || createName is not null || resetName is not null || listAccounts;

var builder = WebApplication.CreateBuilder(args);
if (cliOnly)
{
    // stdout carries only the command's own output (install.sh parses --list-local-accounts); logs go to stderr.
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
}
var extraConfig = Environment.GetEnvironmentVariable("HOMEPAGE_CONFIG");
if (!string.IsNullOrWhiteSpace(extraConfig))
    builder.Configuration.AddJsonFile(extraConfig, optional: false, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("HOMEPAGE_");

builder.Host.UseSystemd();
// UseSystemd detects systemd through /proc/<ppid>/comm, which ProtectProc=invisible hides; with a notify socket,
// use the systemd lifetime anyway so the Type=notify unit sees READY=1.
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOTIFY_SOCKET")) && !cliOnly)
{
    builder.Services.AddSingleton<Microsoft.Extensions.Hosting.Systemd.ISystemdNotifier, Microsoft.Extensions.Hosting.Systemd.SystemdNotifier>();
    builder.Services.AddSingleton<IHostLifetime, Microsoft.Extensions.Hosting.Systemd.SystemdLifetime>();
}

// Relative data/backup folders (development) are relative to the content root, not the current directory.
foreach (var key in new[] { "Paths:Data", "Paths:Backups" })
    if (builder.Configuration[key] is { Length: > 0 } p && !Path.IsPathRooted(p))
        builder.Configuration[key] = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, p));
var site = builder.Configuration.GetSection(SiteOptions.Section).Get<SiteOptions>() ?? new SiteOptions();
var paths = builder.Configuration.GetSection(PathsOptions.Section).Get<PathsOptions>() ?? new PathsOptions();
if (!cliOnly)
{
    builder.WebHost.UseUrls(site.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    if (!string.IsNullOrWhiteSpace(site.Certificate))
    {
        var cert = X509Certificate2.CreateFromPemFile(site.Certificate, string.IsNullOrWhiteSpace(site.CertificateKey) ? null : site.CertificateKey);
        builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = cert));
    }
}
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 120L * 1024 * 1024);

if (site.ForwardedHeaders.Enabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        foreach (var p in site.ForwardedHeaders.KnownProxies.Where(p => !string.IsNullOrWhiteSpace(p))) o.KnownProxies.Add(ForwardedHeadersParsing.ParseProxy(p));
        foreach (var n in site.ForwardedHeaders.KnownNetworks.Where(n => !string.IsNullOrWhiteSpace(n))) o.KnownIPNetworks.Add(ForwardedHeadersParsing.ParseNetwork(n));
    });
}

Directory.CreateDirectory(paths.DataProtectionDirectory);
builder.Services.AddDataProtection()
    .SetApplicationName("Homepage")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.DataProtectionDirectory));

builder.Services.AddHomepageCore(builder.Configuration);
builder.Services.AddHomepageWeb();
if (cliOnly) builder.Services.RemoveAll<IHostedService>();

var app = builder.Build();

// Migrations at every start and in --migrate-only (update.sh runs it before switching releases, so a failure
// rolls back through its pg_dump).
await Startup.MigrateAsync(app.Services);
if (migrateOnly)
{
    Console.WriteLine("Migrations applied.");
    return 0;
}

if (listAccounts)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    foreach (var u in await db.Users.AsNoTracking().OrderBy(u => u.NormalizedUserName).ToListAsync())
        Console.WriteLine($"{u.UserName}\t{(u.Valid ? "enabled" : "disabled")}");
    return 0;
}

if (resetName is not null)
{
    var password = Console.In.ReadLine() ?? "";
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var normalized = resetName.Trim().ToUpperInvariant();
    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUserName == normalized);
    if (user is null)
    {
        Console.Error.WriteLine($"There is no account '{resetName}'. Use --list-local-accounts, or --create-local-account.");
        return 1;
    }
    var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
    var r = await accounts.ResetPasswordAsync(user.Id, password, "install.sh");
    if (!r.Ok)
    {
        Console.Error.WriteLine(r.Error);
        return 1;
    }
    if (!user.Valid) await accounts.SetValidAsync(user.Id, true, "install.sh");
    Console.WriteLine($"The password of '{user.UserName}' was reset{(user.Valid ? "" : " and the account enabled")}.");
    return 0;
}

if (createName is not null)
{
    var password = Console.In.ReadLine() ?? "";
    await using var scope = app.Services.CreateAsyncScope();
    var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
    var (result, _) = await accounts.CreateAsync(createName, createName, password, "install", makeAdmin: true);
    if (!result.Ok)
    {
        Console.Error.WriteLine(result.Error);
        return 1;
    }
    Console.WriteLine($"Account '{createName}' created (admin: rw).");
    return 0;
}

if (site.ForwardedHeaders.Enabled) app.UseForwardedHeaders();

// Health endpoint for update.sh. Loopback only (behind a same-host proxy, forwarded headers are applied first,
// so proxied requests carry the client's address; block /healthz in the proxy as well).
app.MapGet("/healthz", (HttpContext ctx) =>
    // Exact path only: routing would also match /healthz/ and /HEALTHZ, which a proxy rule for /healthz misses.
    ctx.Request.Path.Value == "/healthz" && ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip)
        ? Results.Text($$"""{"status":"ok","version":"{{AppVersion.Current}}"}""", "application/json")
        : Results.NotFound());

app.UseHomepageWeb();

await app.RunAsync();
return 0;

static string? ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
}

public partial class Program;

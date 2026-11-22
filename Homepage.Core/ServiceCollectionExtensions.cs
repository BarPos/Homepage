using Homepage.Accounts;
using Homepage.Common;
using Homepage.Content;
using Homepage.Data;
using Homepage.Media;
using Homepage.Options;
using Homepage.Permissions;
using Homepage.Themes;
using Homepage.Updates;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Homepage;

public static class ServiceCollectionExtensions
{
    public static void ConfigureDb(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, o => o.MigrationsAssembly("Homepage.Server"));

    public static IServiceCollection AddHomepageCore(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SiteOptions>(config.GetSection(SiteOptions.Section));
        services.Configure<PathsOptions>(config.GetSection(PathsOptions.Section));
        services.Configure<UpdatesOptions>(config.GetSection(UpdatesOptions.Section));

        var cs = config.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("ConnectionStrings:Default is not set (PostgreSQL is required).");
        services.AddDbContextFactory<AppDbContext>(o => ConfigureDb(o, cs));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        services.AddIdentityCore<User>(o =>
            {
                o.Password.RequiredLength = AccountService.MinPasswordLength;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                // No account lockout: it lets anyone lock an admin out. Failed sign-ins are limited per network instead
                // (SignInLimiter in Homepage.Web).
                o.Lockout.AllowedForNewUsers = false;
                o.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@";
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        services.AddMemoryCache();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<MarkdownRenderer>();
        services.AddSingleton<ContentExtractor>();
        services.AddSingleton<IAuditLog, AuditLog>();
        services.TryAddSingleton<IAuditRequestInfo, NoAuditRequestInfo>(); // the web host registers the real one first
        services.AddSingleton<IPermissionService, PermissionService>();
        services.AddSingleton<ContentService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<MenuService>();
        services.AddSingleton<BlogService>();
        services.AddSingleton<SiteTree>();
        services.AddSingleton<MediaService>();
        services.AddSingleton<PermissionAdminService>();
        services.AddScoped<AccountService>();

        services.AddHttpClient(GitHubReleaseSource.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(10));
        services.AddSingleton<IReleaseSource, GitHubReleaseSource>();
        services.AddSingleton<IServerInstallation, ServerInstallation>();
        services.AddSingleton<ServerUpdateService>();
        services.AddHostedService<UpdatesBackgroundService>();

        services.Configure<Analytics.AnalyticsOptions>(config.GetSection(Analytics.AnalyticsOptions.Section));
        services.AddSingleton<Analytics.CountryLookup>();
        services.AddSingleton<Analytics.AnalyticsCollector>();
        services.AddSingleton<Analytics.AnalyticsQueries>();
        services.AddHostedService<Analytics.AnalyticsWriter>();
        services.AddHttpClient(Analytics.GeoDatabaseUpdater.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddHostedService<Analytics.GeoDatabaseUpdater>();
        return services;
    }
}

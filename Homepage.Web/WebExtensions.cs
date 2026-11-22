using System.Threading.RateLimiting;
using Homepage.Web.Endpoints;
using Homepage.Web.Rendering;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Hosting;

namespace Homepage.Web;

public static class WebExtensions
{
    public static IServiceCollection AddHomepageWeb(this IServiceCollection services)
    {
        services.AddRazorComponents();
        services.AddHttpContextAccessor();
        services.AddSingleton<Homepage.Common.IAuditRequestInfo, AuditRequestInfo>();
        services.AddScoped<RenderContextFactory>();
        services.AddSingleton<PublicContent>();
        services.AddScoped<AdminFilter>();
        services.AddSingleton<SignInLimiter>();
        services.AddOutputCache(SiteEndpoints.AddPublicCachePolicy);
        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "hp_af";
            o.Cookie.Path = "/admin";
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.HeaderName = "RequestVerificationToken";
        });
        // The admin cookie is scoped to /admin: public pages never see it, so they stay cacheable for everyone.
        services.AddAuthentication(AdminAuth.Scheme)
            .AddCookie(AdminAuth.Scheme, o =>
            {
                o.Cookie.Name = "hp_admin";
                o.Cookie.Path = "/admin";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.LoginPath = "/admin/login";
                o.AccessDeniedPath = "/admin/login";
                o.ExpireTimeSpan = TimeSpan.FromHours(8); // sliding; AdminFilter also ends sessions 12 h after sign-in
                o.SlidingExpiration = true;
            });
        services.AddAuthorization();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("signin", ctx => RateLimitPartition.GetFixedWindowLimiter(
                SignInLimiter.Network(ctx.Connection.RemoteIpAddress), // IPv6 per /64
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy("analytics", ctx => RateLimitPartition.GetFixedWindowLimiter(
                SignInLimiter.Network(ctx.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return services;
    }

    public const string PublicCsp =
        "default-src 'self'; img-src 'self' data: https:; style-src 'self' 'unsafe-inline'; script-src 'self'; " +
        "frame-src https://www.youtube-nocookie.com https://player.vimeo.com; frame-ancestors 'none'; form-action 'self'; base-uri 'self'; object-src 'none'";

    // Alpine.js evaluates its expressions with Function(), hence 'unsafe-eval' in the admin only. No inline scripts or handlers.
    public const string AdminCsp =
        "default-src 'self'; script-src 'self' 'unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "frame-ancestors 'self'; form-action 'self'; base-uri 'self'; object-src 'none'";

    /// <summary>Security headers, output cache, auth and all endpoints. Call after UseForwardedHeaders.</summary>
    public static void UseHomepageWeb(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            var isAdmin = ctx.Request.Path.StartsWithSegments("/admin");
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["X-Frame-Options"] = isAdmin ? "SAMEORIGIN" : "DENY";
            if (!h.ContainsKey("Content-Security-Policy")) h["Content-Security-Policy"] = isAdmin ? AdminCsp : PublicCsp;
            if (isAdmin) h.CacheControl = "no-store";
            await next();
        });
        app.UseStaticFiles();
        // Routing after static files: the public catch-all route (/{**path}) must not claim files such as /_content/….
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.UseOutputCache();
        app.MapAdmin();
        app.MapAnalyticsCollection();
        app.MapSite();
    }
}

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Homepage.Accounts;
using Homepage.Analytics;
using Homepage.Data;
using Homepage.Web.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homepage.Tests;

public class AnalyticsUnitTests
{
    private const string ChromeMac = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string SafariIphone = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1";
    private const string EdgeWindows = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";
    private const string FirefoxAndroid = "Mozilla/5.0 (Android 14; Mobile; rv:132.0) Gecko/132.0 Firefox/132.0";

    [Fact]
    public void UserAgentsGiveBrowserAndSystemAndSpotBots()
    {
        Assert.Equal(("Chrome", "macOS"), UserAgents.Parse(ChromeMac));
        Assert.Equal(("Safari", "iOS"), UserAgents.Parse(SafariIphone));
        Assert.Equal(("Edge", "Windows"), UserAgents.Parse(EdgeWindows));
        Assert.Equal(("Firefox", "Android"), UserAgents.Parse(FirefoxAndroid));
        Assert.False(UserAgents.IsBot(ChromeMac));
        Assert.True(UserAgents.IsBot("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)"));
        Assert.True(UserAgents.IsBot("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.2; +https://openai.com/gptbot)"));
        Assert.True(UserAgents.IsBot("curl/8.5.0"));
        Assert.True(UserAgents.IsBot(""));
        Assert.Equal("Mobile", UserAgents.Device(390));
        Assert.Equal("Tablet", UserAgents.Device(800));
        Assert.Equal("Laptop", UserAgents.Device(1280));
        Assert.Equal("Desktop", UserAgents.Device(1920));
    }

    [Fact]
    public void ReferrersBecomeSourcesAndChannels()
    {
        Assert.Equal("news.ycombinator.com", Referrers.Host("https://news.ycombinator.com/item?id=1"));
        Assert.Equal("example.org", Referrers.Host("https://www.Example.org/a"));
        Assert.Null(Referrers.Host("javascript:alert(1)"));
        Assert.Equal(("Google", Channels.Search), Referrers.Classify("google.de", null, null));
        Assert.Equal(("Hacker News", Channels.Social), Referrers.Classify("news.ycombinator.com", null, null));
        Assert.Equal(("X (Twitter)", Channels.Social), Referrers.Classify("t.co", null, null));
        Assert.Equal(("blog.example.org", Channels.Referral), Referrers.Classify("blog.example.org", null, null));
        Assert.Equal(("newsletter", Channels.Email), Referrers.Classify(null, "newsletter", "email"));
        Assert.Equal(("spring", Channels.Campaign), Referrers.Classify(null, "spring", null));
        Assert.Equal(("Google", Channels.Paid), Referrers.Classify("google.com", null, "cpc"));
        Assert.Equal((null, Channels.Direct), Referrers.Classify(null, null, null));
        Assert.Equal(("notgoogle.example", Channels.Referral), Referrers.Classify("notgoogle.example", null, null));
        Assert.Equal(("Gmail", Channels.Email), Referrers.Classify("mail.google.com", null, null));
    }

    [Fact]
    public void UrlsAreCleanedAndEventsValidated()
    {
        Assert.Equal(("/blog/hello", "utm_source=x"), AnalyticsWriter.SplitUrl("/blog/hello/?utm_source=x"));
        Assert.Equal(("/café", ""), AnalyticsWriter.SplitUrl("/caf%C3%A9#top"));
        Assert.NotNull(AnalyticsEndpoints.Parse("""{"t":"v","u":"/a","r":null,"w":1280,"k":"abcdef1234"}"""));
        Assert.Null(AnalyticsEndpoints.Parse("""{"t":"v","u":"/admin/users","k":"abcdef1234"}"""));
        Assert.Null(AnalyticsEndpoints.Parse("""{"t":"v","u":"//evil.example/","k":"abcdef1234"}"""));
        Assert.Null(AnalyticsEndpoints.Parse("""{"t":"v","u":"/a","k":"short"}"""));
        Assert.Null(AnalyticsEndpoints.Parse("""{"t":"x","u":"/a","k":"abcdef1234"}"""));
        Assert.Null(AnalyticsEndpoints.Parse("not json"));
        Assert.Equal(3600, AnalyticsEndpoints.Parse("""{"t":"e","u":"/a","k":"abcdef1234","s":3600}""")!.Seconds);
    }

    [Fact]
    public void CountryLookupReadsAMaxMindDatabase()
    {
        // The dev server downloads the DB-IP file to .devdata/data/geo; without it there is nothing to read (null, no error).
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, ".devdata"))) root = Path.GetDirectoryName(root);
        var file = root is null ? null : Directory.EnumerateFiles(Path.Combine(root, ".devdata"), "dbip-country-lite.mmdb", SearchOption.AllDirectories).FirstOrDefault();
        var lookup = new CountryLookup(Microsoft.Extensions.Options.Options.Create(new Homepage.Options.PathsOptions { Data = Path.GetTempPath() }),
            Microsoft.Extensions.Options.Options.Create(new AnalyticsOptions { GeoDatabase = file ?? "/nonexistent/geo.mmdb" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CountryLookup>.Instance);
        Assert.Null(lookup.Find(System.Net.IPAddress.Loopback));
        if (file is null) { Assert.Null(lookup.Find(System.Net.IPAddress.Parse("8.8.8.8"))); return; }
        Assert.Equal("US", lookup.Find(System.Net.IPAddress.Parse("8.8.8.8")));
        Assert.Equal("US", lookup.Find(System.Net.IPAddress.Parse("::ffff:8.8.8.8")));
    }

    [Fact]
    public void RangesFollowTheSiteTimeZone()
    {
        var tz = AnalyticsSettings.TryTimeZone("Europe/Warsaw")!;
        var now = new DateTime(2026, 11, 22, 23, 30, 0, DateTimeKind.Utc); // 00:30 on 23 Nov in Warsaw
        var today = AnalyticsRange.Resolve("today", null, null, tz, now);
        Assert.Equal(new DateTime(2026, 11, 23), today.FromLocal);
        Assert.Equal(new DateTime(2026, 11, 22, 23, 0, 0, DateTimeKind.Utc), today.FromUtc);
        Assert.Equal("hour", today.Unit);
        var d30 = AnalyticsRange.Resolve("30d", null, null, tz, now);
        Assert.Equal(30, (d30.ToLocal - d30.FromLocal).TotalDays);
        Assert.Equal("day", d30.Unit);
        Assert.Equal("month", AnalyticsRange.Resolve("12mo", null, null, tz, now).Unit);
        var custom = AnalyticsRange.Resolve("custom", "2026-01-01", "2026-01-31", tz, now);
        Assert.Equal((new DateTime(2026, 1, 1), new DateTime(2026, 2, 1)), (custom.FromLocal, custom.ToLocal));
        Assert.Equal("30d", AnalyticsRange.Resolve("nonsense", null, null, tz, now).Key);
        var prev = d30.Previous(tz, now);
        Assert.Equal(d30.FromLocal.AddDays(-30), prev.FromLocal);
        // a running period is compared with the same elapsed time before it: today 00:00–00:30 with yesterday 00:00–00:30
        var prevToday = today.Previous(tz, now);
        Assert.Equal((new DateTime(2026, 11, 22), new DateTime(2026, 11, 22, 0, 30, 0)), (prevToday.FromLocal, prevToday.ToLocal));
    }
}

/// <summary>Over HTTP: the script's events become page views, visits and reports.</summary>
[Collection("Postgres"), Trait("Category", "Postgres")]
public sealed class AnalyticsHttpTests(PostgresFixture pg)
{
    private const string Ua = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string Password = "test-password-123";

    [Fact]
    public async Task CountsVisitsAndReportsThem()
    {
        var cs = await pg.CreateDatabaseAsync();
        var data = Directory.CreateTempSubdirectory("homepage-test-").FullName;
        Environment.SetEnvironmentVariable("HOMEPAGE_ConnectionStrings__Default", cs);
        Environment.SetEnvironmentVariable("HOMEPAGE_Paths__Data", data);
        try
        {
            await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
            using (var scope = app.Services.CreateScope())
                Assert.True((await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync("admin", "Admin", Password, "test", makeAdmin: true)).Result.Ok);
            var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

            // public pages include the script (counting is on by default)
            Assert.Contains("js/hp-analytics.js", await client.GetStringAsync("/"));

            async Task<HttpStatusCode> Send(string json, string origin = "http://localhost", string ua = Ua)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "/hp/event") { Content = new StringContent(json, Encoding.UTF8, "text/plain") };
                req.Headers.TryAddWithoutValidation("User-Agent", ua);
                req.Headers.TryAddWithoutValidation("Origin", origin);
                return (await client.SendAsync(req)).StatusCode;
            }
            Assert.Equal(HttpStatusCode.NoContent, await Send("""{"t":"v","u":"/?utm_source=newsletter&utm_medium=email","r":"https://www.google.com/","w":390,"k":"page000001"}"""));
            await Send("""{"t":"v","u":"/blog/hello-world","r":"http://localhost/","w":390,"k":"page000002"}""");
            await Send("""{"t":"e","u":"/blog/hello-world","k":"page000002","s":42}""");
            // ignored: bots, other sites, admin pages, junk
            await Send("""{"t":"v","u":"/","r":null,"w":1280,"k":"page000003"}""", ua: "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)");
            await Send("""{"t":"v","u":"/","r":null,"w":1280,"k":"page000004"}""", origin: "https://evil.example");
            await Send("""{"t":"v","u":"/admin","r":null,"w":1280,"k":"page000005"}""");
            await Send("""{"t":"v","u":"/x","k":"!"}""");

            var factory = app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
            List<PageView> views = [];
            for (var i = 0; i < 50; i++)
            {
                await using var db = await factory.CreateDbContextAsync();
                views = await db.PageViews.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
                if (views.Count == 2 && views[1].EngagedSeconds == 42) break;
                await Task.Delay(100);
            }
            Assert.Equal(2, views.Count);
            var (first, second) = (views[0], views[1]);
            Assert.Equal(("/", true, "newsletter", Channels.Email, "google.com"), (first.Path, first.IsEntry, first.Source, first.Channel, first.ReferrerHost));
            Assert.Equal(("Chrome", "macOS", "Mobile"), (first.Browser, first.Os, first.Device));
            Assert.Equal(first.VisitorId, second.VisitorId);
            Assert.Equal(first.SessionId, second.SessionId);
            Assert.False(second.IsEntry);
            // an internal link: the page keeps the visit's source
            Assert.Equal(("newsletter", Channels.Email, "google.com"), (second.Source, second.Channel, second.ReferrerHost));
            Assert.Equal(42, second.EngagedSeconds);

            // reports
            var token = Token(await client.GetStringAsync("/admin/login"));
            await client.PostAsync("/admin/login", Form(("__RequestVerificationToken", token), ("userName", "admin"), ("password", Password)));
            var page = await client.GetStringAsync("/admin/analytics?range=today");
            token = Token(page);
            Assert.Contains("1 visitor now", page);
            Assert.Contains("/blog/hello-world", page);
            Assert.Matches(@"Unique visitors</span>\s*<strong>1</strong>", page);
            Assert.Matches(@"Page views</span>\s*<strong>2</strong>", page);
            Assert.Matches(@"Bounce rate</span>\s*<strong>0%</strong>", page);
            Assert.Contains("newsletter", page);
            Assert.Contains("Visitors today", await client.GetStringAsync("/admin"));
            var filtered = await client.GetStringAsync("/admin/analytics?range=today&page=%2Fblog%2Fhello-world");
            Assert.Matches(@"Page views</span>\s*<strong>1</strong>", filtered);
            Assert.Contains("Showing only", filtered);
            var csv = await client.GetStringAsync("/admin/analytics/export?range=today&dim=page");
            Assert.StartsWith("page,visitors,pageviews\n", csv);
            Assert.Contains("/blog/hello-world,1,1", csv);
            Assert.Contains("newsletter,1,2", await client.GetStringAsync("/admin/analytics/export?range=today&dim=source"));
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/admin/analytics/export?dim=Path;drop")).StatusCode);
            Assert.Contains("visitor", await client.GetStringAsync("/admin/analytics/realtime"));

            // settings: a bad time zone is refused; turning counting off removes the script and stops collection
            await client.PostAsync("/admin/analytics/settings", Form(("__RequestVerificationToken", token), ("enabled", "true"), ("countries", "true"),
                ("retentionDays", "365"), ("timeZone", "Mars/Olympus")));
            Assert.Contains("not a time zone", await client.GetStringAsync("/admin/analytics"));
            await client.PostAsync("/admin/analytics/settings", Form(("__RequestVerificationToken", token), ("retentionDays", "365"), ("timeZone", "Europe/Warsaw")));
            Assert.DoesNotContain("js/hp-analytics.js", await client.GetStringAsync("/"));
            Assert.Contains("Analytics is off", await client.GetStringAsync("/admin/analytics"));

            // long periods: month buckets line up with data also when the period starts mid-month
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.PageViews.Add(new PageView { AtUtc = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc), VisitorId = 77, SessionId = 77, IsEntry = true, Path = "/old", PageKey = "oldpage01" });
                await db.SaveChangesAsync();
            }
            var queries = app.Services.GetRequiredService<AnalyticsQueries>();
            var utc = AnalyticsSettings.TryTimeZone("UTC")!;
            var longRange = AnalyticsRange.Resolve("custom", "2026-01-15", "2026-06-20", utc, DateTime.UtcNow);
            Assert.Equal("month", longRange.Unit);
            var series = await queries.TimeseriesAsync(longRange, new Dictionary<string, string>(), "UTC");
            Assert.Equal(1, series.Single(p => p.Local == new DateTime(2026, 3, 1)).Visitors);
            Assert.Equal(6, series.Count); // Jan … Jun
            Assert.Contains("1 visitors, 1 page views", await client.GetStringAsync("/admin/analytics?range=custom&from=2026-01-15&to=2026-06-20"));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/analytics?range=12mo")).StatusCode);

            // someone without analytics: nothing to see or change
            int contentGroup;
            await using (var db = await factory.CreateDbContextAsync()) contentGroup = await db.Groups.Where(g => g.Name == "content").Select(g => g.Id).SingleAsync();
            using (var scope = app.Services.CreateScope())
            {
                var (r, editor) = await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync("editor", "Editor", Password, "test");
                Assert.True(r.Ok);
                await using var db = await factory.CreateDbContextAsync();
                db.UserGroups.Add(new UserGroup { UserId = editor!.Id, GroupId = contentGroup, Permission = Homepage.Permissions.PermNames.ToKey(Homepage.Permissions.Perm.Rw) });
                await db.SaveChangesAsync();
            }
            var other = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
            var t2 = Token(await other.GetStringAsync("/admin/login"));
            Assert.Equal(HttpStatusCode.Redirect, (await other.PostAsync("/admin/login", Form(("__RequestVerificationToken", t2), ("userName", "editor"), ("password", Password)))).StatusCode);
            var dash = await other.GetStringAsync("/admin");
            t2 = Token(dash);
            Assert.DoesNotContain("Visitors today", dash);
            Assert.DoesNotContain("/admin/analytics", dash);
            foreach (var get in new[] { "/admin/analytics", "/admin/analytics/realtime", "/admin/analytics/export?dim=page" })
                Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(get)).StatusCode);
            foreach (var post in new[] { "/admin/analytics/settings", "/admin/analytics/delete" })
                Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync(post, Form(("__RequestVerificationToken", t2), ("timeZone", "UTC")))).StatusCode);

            // delete everything
            await client.PostAsync("/admin/analytics/delete", Form(("__RequestVerificationToken", token)));
            await using (var db = await factory.CreateDbContextAsync()) Assert.Equal(0, await db.PageViews.CountAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOMEPAGE_ConnectionStrings__Default", null);
            Environment.SetEnvironmentVariable("HOMEPAGE_Paths__Data", null);
        }
    }

    private static string Token(string html) =>
        Regex.Match(html, "name=\"(?:__RequestVerificationToken|csrf-token)\" (?:value|content)=\"([^\"]+)\"").Groups[1].Value is { Length: > 0 } t
            ? t : throw new InvalidOperationException("No antiforgery token in the page.");

    private static FormUrlEncodedContent Form(params (string, string)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
}

using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Homepage.Accounts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Homepage.Tests;

/// <summary>Over HTTP: the profile (name, email, password, two-factor), the list + editor pages, and post authors.</summary>
[Collection("Postgres"), Trait("Category", "Postgres")]
public sealed class AccountTests(PostgresFixture pg)
{
    private const string Password = "test-password-123";

    [Fact]
    public async Task ProfileTwoFactorAndAuthors()
    {
        await using var host = await HostAsync();
        var app = host.App;
        var client = NewClient(app);
        var token = await SignInAsync(client, "admin", Password);

        // display name and email; the post shows the author
        var bad = await client.PostAsync("/admin/profile", Form(("__RequestVerificationToken", token), ("displayName", "Ada"), ("email", "not an email")));
        Assert.Equal(HttpStatusCode.Redirect, bad.StatusCode);
        Assert.Contains("does not look right", await client.GetStringAsync("/admin/profile"));
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/admin/profile",
            Form(("__RequestVerificationToken", token), ("displayName", "Ada Lovelace"), ("email", "ada@example.org")))).StatusCode);
        var profile = await client.GetStringAsync("/admin/profile");
        Assert.Contains("value=\"ada@example.org\"", profile);
        Assert.Contains(">Ada Lovelace</a>", profile); // the top bar links to the profile

        var dashboard = await client.GetStringAsync("/admin");
        var gm = Regex.Match(dashboard, "name=\"groupId\"(?:\\s+value=\"(\\d+)\"|>\\s*<option value=\"(\\d+)\")");
        var groupId = gm.Groups[1].Success ? gm.Groups[1].Value : gm.Groups[2].Value;
        var blogId = Regex.Match(dashboard, "name=\"blogId\">\\s*<option value=\"(\\d+)\"").Groups[1].Value;
        var created = await client.PostAsync("/admin/content/new", Form(("__RequestVerificationToken", token), ("title", "Hello"), ("type", "Post"),
            ("groupId", groupId), ("blogId", blogId)));
        var id = created.Headers.Location!.ToString().Split('-').Last();
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync($"/admin/content/{id}/publish", Form(("__RequestVerificationToken", token)))).StatusCode);
        var post = await client.GetStringAsync("/blog/hello");
        Assert.Contains("By <strong>Ada Lovelace</strong>", post);
        Assert.Contains("<meta name=\"author\" content=\"Ada Lovelace\"", post);
        Assert.Contains("Ada Lovelace", await client.GetStringAsync("/blog"));
        Assert.Contains("<dc:creator>Ada Lovelace</dc:creator>", await client.GetStringAsync("/blog/feed.xml"));

        // password change: a wrong current password is refused; the session survives the change
        await client.PostAsync("/admin/profile/password", Form(("__RequestVerificationToken", token), ("current", "wrong-password-1"), ("password", "new-password-456"), ("confirm", "new-password-456")));
        Assert.Contains("current password is not right", await client.GetStringAsync("/admin/profile"));
        await client.PostAsync("/admin/profile/password", Form(("__RequestVerificationToken", token), ("current", Password), ("password", "new-password-456"), ("confirm", "new-password-456")));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/profile")).StatusCode);

        // two-factor: set up with a code from the key on the page
        var setup = await client.GetStringAsync("/admin/profile/2fa");
        Assert.Contains("<svg", setup);
        var key = Regex.Match(setup, "<code class=\"key\">([^<]+)</code>").Groups[1].Value.Replace(" ", "").ToUpperInvariant();
        await client.PostAsync("/admin/profile/2fa", Form(("__RequestVerificationToken", token), ("code", "000000")));
        Assert.Contains("code is not right", await client.GetStringAsync("/admin/profile/2fa"));
        var on = await client.PostAsync("/admin/profile/2fa", Form(("__RequestVerificationToken", token), ("code", Totp(key))));
        var codesPage = await on.Content.ReadAsStringAsync();
        var codes = Regex.Matches(codesPage, "<li><code>([^<]+)</code></li>").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(10, codes.Count);
        Assert.Contains("badge badge-ok\">On", await client.GetStringAsync("/admin/profile")); // still signed in

        // a new sign-in asks for the code; the password alone does not get in
        var second = NewClient(app);
        var t2 = Token(await second.GetStringAsync("/admin/login"));
        var step1 = await second.PostAsync("/admin/login", Form(("__RequestVerificationToken", t2), ("userName", "admin"), ("password", "new-password-456")));
        Assert.Equal("/admin/login/2fa", step1.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await second.GetAsync("/admin")).StatusCode);
        t2 = Token(await second.GetStringAsync("/admin/login/2fa"));
        var wrong = await second.PostAsync("/admin/login/2fa", Form(("__RequestVerificationToken", t2), ("code", "123456")));
        Assert.Equal(HttpStatusCode.Redirect, (await second.GetAsync("/admin")).StatusCode);
        t2 = Token(await second.GetStringAsync("/admin/login/2fa"));
        var ok = await second.PostAsync("/admin/login/2fa", Form(("__RequestVerificationToken", t2), ("code", Totp(key))));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/admin")).StatusCode);

        // a recovery code works once
        var third = NewClient(app);
        var t3 = Token(await third.GetStringAsync("/admin/login"));
        await third.PostAsync("/admin/login", Form(("__RequestVerificationToken", t3), ("userName", "admin"), ("password", "new-password-456")));
        t3 = Token(await third.GetStringAsync("/admin/login/2fa"));
        await third.PostAsync("/admin/login/2fa", Form(("__RequestVerificationToken", t3), ("code", codes[0])));
        Assert.Equal(HttpStatusCode.OK, (await third.GetAsync("/admin")).StatusCode);
        Assert.Contains("<strong>9</strong> unused recovery codes", await third.GetStringAsync("/admin/profile"));

        // turning it off needs the password
        await client.PostAsync("/admin/profile/2fa/disable", Form(("__RequestVerificationToken", token), ("password", "wrong-password-1")));
        Assert.Contains("badge badge-ok\">On", await client.GetStringAsync("/admin/profile"));
        await client.PostAsync("/admin/profile/2fa/disable", Form(("__RequestVerificationToken", token), ("password", "new-password-456")));
        Assert.Contains("class=\"badge\">Off", await client.GetStringAsync("/admin/profile"));
    }

    [Fact]
    public async Task ListsOpenEditorsForNewMenusBlogsAccountsGroupsAndRoles()
    {
        await using var host = await HostAsync();
        var client = NewClient(host.App);
        var token = await SignInAsync(client, "admin", Password);

        // menus: an empty editor, created with its links in one go
        Assert.Contains("href=\"/admin/menus/new\"", await client.GetStringAsync("/admin/menus"));
        Assert.Contains("Create menu", await client.GetStringAsync("/admin/menus/new"));
        var menu = await client.PostAsync("/admin/menus", Form(("__RequestVerificationToken", token), ("name", "Social"), ("key", ""),
            ("label", "Mastodon"), ("url", "https://example.social/@me"), ("label", "Mail"), ("url", "mailto:me@example.org")));
        Assert.Equal("/admin/menus/social", menu.Headers.Location!.ToString());
        var menuPage = await client.GetStringAsync("/admin/menus/social");
        Assert.Contains("Mastodon", menuPage);
        Assert.Contains("Mastodon · Mail", System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/admin/menus")));
        var badLink = await client.PostAsync("/admin/menus", Form(("__RequestVerificationToken", token), ("name", "Evil"), ("label", "x"), ("url", "javascript:alert(1)")));
        var badBody = await badLink.Content.ReadAsStringAsync();
        Assert.Contains("not an allowed link", badBody);
        Assert.Contains("value=\"Evil\"", badBody); // what was typed stays
        Assert.DoesNotContain("/admin/menus/evil", await client.GetStringAsync("/admin/menus"));
        Assert.Contains("cannot be a menu key", await (await client.PostAsync("/admin/menus", Form(("__RequestVerificationToken", token), ("name", "New")))).Content.ReadAsStringAsync());

        // blogs
        Assert.Contains("Create blog", await client.GetStringAsync("/admin/blogs/new"));
        var blog = await client.PostAsync("/admin/blogs", Form(("__RequestVerificationToken", token), ("name", "Releases"), ("slug", "releases"), ("description", ""), ("postsPerPage", "10")));
        Assert.Matches("^/admin/blogs/\\d+$", blog.Headers.Location!.ToString());
        Assert.Contains("value=\"Releases\"", await client.GetStringAsync(blog.Headers.Location!.ToString()));
        var taken = await client.PostAsync("/admin/blogs", Form(("__RequestVerificationToken", token), ("name", "Again"), ("slug", "releases"), ("postsPerPage", "10")));
        Assert.Contains("value=\"Again\"", await taken.Content.ReadAsStringAsync());

        // accounts
        Assert.Contains("href=\"/admin/users/new\"", await client.GetStringAsync("/admin/users"));
        Assert.Contains("Create account", await client.GetStringAsync("/admin/users/new"));
        var user = await client.PostAsync("/admin/users", Form(("__RequestVerificationToken", token), ("userName", "grace"), ("displayName", "Grace"),
            ("email", "grace@example.org"), ("password", "grace-password-1")));
        Assert.Matches("^/admin/users/\\d+$", user.Headers.Location!.ToString());
        Assert.Contains("value=\"grace@example.org\"", await client.GetStringAsync(user.Headers.Location!.ToString()));
        Assert.Contains("grace@example.org", await client.GetStringAsync("/admin/users?q=example.org"));

        // groups and roles
        var group = await client.PostAsync("/admin/groups", Form(("__RequestVerificationToken", token), ("name", "marketing"), ("comment", "Campaigns"), ("valid", "true")));
        Assert.Matches("^/admin/groups/\\d+$", group.Headers.Location!.ToString());
        Assert.Contains("In this group: 0 pages and posts", await client.GetStringAsync(group.Headers.Location!.ToString()));
        var role = await client.PostAsync("/admin/roles", Form(("__RequestVerificationToken", token), ("name", "Editors"), ("comment", ""), ("valid", "true")));
        Assert.Matches("^/admin/roles/\\d+$", role.Headers.Location!.ToString());
        Assert.Contains("href=\"/admin/roles/new\"", await client.GetStringAsync("/admin/roles"));

        // theme editor: saving keeps a draft (badge, Publish button) and does not touch the live site
        var themes = await client.GetStringAsync("/admin/themes");
        var builtIn = Regex.Match(themes, "/admin/themes/(\\d+)/duplicate").Groups[1].Value;
        var copy = await client.PostAsync($"/admin/themes/{builtIn}/duplicate", Form(("__RequestVerificationToken", token)));
        var editUrl = copy.Headers.Location!.ToString();
        var editor = await client.GetStringAsync(editUrl);
        Assert.Contains("Save draft", editor);
        Assert.DoesNotContain("Unpublished changes", editor);
        var name = Regex.Match(editor, "id=\"tn\" name=\"name\" type=\"text\" value=\"([^\"]+)\"").Groups[1].Value;
        await client.PostAsync(editUrl, Form(("__RequestVerificationToken", token), ("name", name), ("customCss", "body{outline:0}")));
        editor = await client.GetStringAsync(editUrl);
        Assert.Contains("Unpublished changes", editor);
        Assert.Contains($"action=\"{editUrl}/publish\"", editor);
        Assert.Contains("Unpublished changes", await client.GetStringAsync("/admin/themes"));

        // the footer of the active theme, edited block by block: no audit entries and no live change until Publish
        var themeId = editUrl.Split('/').Last();
        await client.PostAsync($"/admin/themes/{themeId}/discard", Form(("__RequestVerificationToken", token)));
        await client.PostAsync($"/admin/themes/{themeId}/activate", Form(("__RequestVerificationToken", token)));
        int ThemeAuditRows()
        {
            using var scope = host.App.Services.CreateScope();
            using var db = scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Homepage.Data.AppDbContext>>().CreateDbContext();
            return db.AuditLog.Count(x => x.Action.StartsWith("theme.") && x.Action != "theme.activate" && x.Action != "theme.duplicate" && x.Action != "theme.discard");
        }
        var before = ThemeAuditRows();
        var target = $"footer-{themeId}";
        var op = new HttpRequestMessage(HttpMethod.Post, $"/admin/editor/{target}/ops")
        {
            Content = new StringContent("""{"op":"insert","type":"markdown","parentId":null,"index":0}""", System.Text.Encoding.UTF8, "application/json"),
        };
        op.Headers.Add("RequestVerificationToken", token);
        op.Headers.Add("Accept", "application/json");
        var opBody = await (await client.SendAsync(op)).Content.ReadAsStringAsync();
        var blockId = Regex.Match(opBody, "\"newId\":\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(blockId);
        foreach (var text in new[] { "F", "Fo", "Footer note", "Footer note 4711" }) // typing
        {
            var props = new HttpRequestMessage(HttpMethod.Post, $"/admin/editor/{target}/props/{blockId}") { Content = Form(("p.Markdown", text)) };
            props.Headers.Add("RequestVerificationToken", token);
            props.Headers.Add("HX-Request", "true");
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(props)).StatusCode);
        }
        Assert.Equal(before, ThemeAuditRows());
        Assert.DoesNotContain("Footer note 4711", await client.GetStringAsync("/"));
        Assert.Contains("Footer note 4711", await client.GetStringAsync($"/admin/editor/{target}/canvas")); // the editor shows the draft
        Assert.Contains($"hx-post=\"/admin/themes/{themeId}/publish\"", await client.GetStringAsync($"/admin/editor/{target}"));
        await client.PostAsync($"/admin/themes/{themeId}/publish", Form(("__RequestVerificationToken", token)));
        Assert.Equal(before + 1, ThemeAuditRows());
        Assert.Contains("Footer note 4711", await client.GetStringAsync("/"));
    }

    // ------------------------------------------------------------------ helpers

    private sealed class Host(WebApplicationFactory<Program> app, string data) : IAsyncDisposable
    {
        public WebApplicationFactory<Program> App => app;
        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            Environment.SetEnvironmentVariable("HOMEPAGE_ConnectionStrings__Default", null);
            Environment.SetEnvironmentVariable("HOMEPAGE_Paths__Data", null);
            try { Directory.Delete(data, true); } catch (IOException) { }
        }
    }

    private async Task<Host> HostAsync()
    {
        var cs = await pg.CreateDatabaseAsync();
        var data = Directory.CreateTempSubdirectory("homepage-test-").FullName;
        Environment.SetEnvironmentVariable("HOMEPAGE_ConnectionStrings__Default", cs);
        Environment.SetEnvironmentVariable("HOMEPAGE_Paths__Data", data);
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
        using (var scope = app.Services.CreateScope())
        {
            var (r, _) = await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync("admin", "Admin", Password, "test", makeAdmin: true);
            Assert.True(r.Ok, r.Error);
        }
        return new Host(app, data);
    }

    private static HttpClient NewClient(WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<string> SignInAsync(HttpClient client, string user, string password)
    {
        var token = Token(await client.GetStringAsync("/admin/login"));
        var login = await client.PostAsync("/admin/login", Form(("__RequestVerificationToken", token), ("userName", user), ("password", password)));
        Assert.Equal("/admin", login.Headers.Location?.ToString());
        return Token(await client.GetStringAsync("/admin"));
    }

    /// <summary>RFC 6238 code (SHA-1, 30 s, 6 digits) for a base32 key, as an authenticator app computes it.</summary>
    private static string Totp(string base32Key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(base32Key.TrimEnd('=').Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24 | hash[offset + 1] << 16 | hash[offset + 2] << 8 | hash[offset + 3]) % 1_000_000;
        return code.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Token(string html) =>
        Regex.Match(html, "name=\"(?:__RequestVerificationToken|csrf-token)\" (?:value|content)=\"([^\"]+)\"").Groups[1].Value is { Length: > 0 } t
            ? t : throw new InvalidOperationException("No antiforgery token in the page.");

    private static FormUrlEncodedContent Form(params (string, string)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
}

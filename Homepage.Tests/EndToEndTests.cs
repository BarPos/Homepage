using System.Net;
using System.Text.RegularExpressions;
using Homepage.Accounts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Homepage.Tests;

/// <summary>The real host over HTTP: sign in, create a page, edit it through the editor endpoints, publish, view it.</summary>
[Collection("Postgres"), Trait("Category", "Postgres")]
public sealed class EndToEndTests(PostgresFixture pg)
{
    [Fact]
    public async Task SignInCreateEditPublishView()
    {
        var cs = await pg.CreateDatabaseAsync();
        var data = Directory.CreateTempSubdirectory("homepage-test-").FullName;
        Environment.SetEnvironmentVariable("HOMEPAGE_ConnectionStrings__Default", cs);
        Environment.SetEnvironmentVariable("HOMEPAGE_Paths__Data", data);
        try
        {
            await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
            const string password = "test-password-123";
            using (var scope = app.Services.CreateScope())
            {
                var (r, _) = await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync("admin", "Admin", password, "test", makeAdmin: true);
                Assert.True(r.Ok, r.Error);
            }
            var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

            // public site works anonymously, admin does not
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin")).StatusCode);

            // sign in (antiforgery token from the form)
            var token = Token(await client.GetStringAsync("/admin/login"));
            var login = await client.PostAsync("/admin/login", Form(("__RequestVerificationToken", token), ("userName", "admin"), ("password", password)));
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            var dashboard = await client.GetStringAsync("/admin");
            token = Token(dashboard);
            var groupId = Regex.Match(dashboard, "name=\"groupId\">\\s*<option value=\"(\\d+)\"").Groups[1].Value;

            // a POST without the token is refused
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/admin/content/new", Form(("title", "x"), ("type", "Page"), ("groupId", groupId)))).StatusCode);

            var created = await client.PostAsync("/admin/content/new", Form(("__RequestVerificationToken", token), ("title", "Contact"), ("type", "Page"), ("groupId", groupId)));
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
            var target = created.Headers.Location!.ToString().Split('/').Last();
            Assert.StartsWith("content-", target);

            // editor: insert a markdown block, edit it through the inspector form, publish
            var op = new HttpRequestMessage(HttpMethod.Post, $"/admin/editor/{target}/ops")
            {
                Content = new StringContent("""{"op":"insert","type":"markdown","parentId":null,"index":1}""", System.Text.Encoding.UTF8, "application/json"),
            };
            op.Headers.Add("RequestVerificationToken", token);
            op.Headers.Add("Accept", "application/json");
            var opRes = await client.SendAsync(op);
            var opBody = await opRes.Content.ReadAsStringAsync();
            Assert.True(opRes.IsSuccessStatusCode, opBody);
            var blockId = Regex.Match(opBody, "\"newId\":\"([^\"]+)\"").Groups[1].Value;

            var props = new HttpRequestMessage(HttpMethod.Post, $"/admin/editor/{target}/props/{blockId}")
            {
                Content = Form(("p.Markdown", "Write to **hello@example.com**")),
            };
            props.Headers.Add("RequestVerificationToken", token);
            props.Headers.Add("HX-Request", "true");
            var propsRes = await client.SendAsync(props);
            Assert.Equal(HttpStatusCode.NoContent, propsRes.StatusCode);
            Assert.Equal("hp-changed", propsRes.Headers.GetValues("HX-Trigger").Single());

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/contact")).StatusCode); // not published yet
            var pub = new HttpRequestMessage(HttpMethod.Post, $"/admin/content/{target["content-".Length..]}/publish") { Content = Form() };
            pub.Headers.Add("RequestVerificationToken", token);
            pub.Headers.Add("HX-Request", "true");
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(pub)).StatusCode);

            var page = await client.GetStringAsync("/contact");
            Assert.Contains("<strong>hello@example.com</strong>", page);
            Assert.Contains("/theme/", page);

            // status messages cannot be injected through the URL; a real one shows once
            Assert.DoesNotContain("Pwned-by-link", await client.GetStringAsync("/admin/settings?message=Pwned-by-link&error=Pwned-by-link"));
            Assert.DoesNotContain("Pwned-by-link", await client.GetStringAsync("/admin/users/1?message=Pwned-by-link"));
            var settingsPage = await client.GetStringAsync("/admin/settings");
            var save = await client.PostAsync("/admin/settings", Form(("__RequestVerificationToken", Token(settingsPage)), ("title", "My site"), ("tagline", ""),
                ("baseUrl", ""), ("home", ""), ("defaultGroupId", groupId), ("logoMediaId", ""), ("faviconMediaId", "")));
            Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
            Assert.Equal("/admin/settings", save.Headers.Location!.ToString());
            Assert.Contains("Saved.", await client.GetStringAsync("/admin/settings"));
            Assert.DoesNotContain("Saved.", await client.GetStringAsync("/admin/settings"));

            // the update progress dialog talks JSON: status, and a readable refusal off Ubuntu installs
            var status = await client.GetStringAsync("/admin/updates/status");
            Assert.Contains($"\"version\":\"{Homepage.Shared.AppVersion.Current}\"", status);
            var apply = new HttpRequestMessage(HttpMethod.Post, "/admin/updates/apply") { Content = Form(("__RequestVerificationToken", Token(settingsPage))) };
            apply.Headers.Add("Accept", "application/json");
            var applied = await client.SendAsync(apply);
            Assert.Equal(HttpStatusCode.BadRequest, applied.StatusCode);
            Assert.Contains("\"error\":", await applied.Content.ReadAsStringAsync());

            // security headers
            var head = await client.GetAsync("/contact");
            Assert.Contains("frame-ancestors 'none'", head.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("nosniff", head.Headers.GetValues("X-Content-Type-Options").Single());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOMEPAGE_ConnectionStrings__Default", null);
            Environment.SetEnvironmentVariable("HOMEPAGE_Paths__Data", null);
        }
    }

    [Fact]
    public async Task AuthorIsLimitedByGroupPermissions()
    {
        var cs = await pg.CreateDatabaseAsync();
        var data = Directory.CreateTempSubdirectory("homepage-test-").FullName;
        Environment.SetEnvironmentVariable("HOMEPAGE_ConnectionStrings__Default", cs);
        Environment.SetEnvironmentVariable("HOMEPAGE_Paths__Data", data);
        try
        {
            await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
            using (var scope = app.Services.CreateScope())
                Assert.True((await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync("admin", "Admin", "admin-password-1", "test", makeAdmin: true)).Result.Ok);
            var admin = await SignInAsync(app, "admin", "admin-password-1");
            var adminToken = Token(await admin.GetStringAsync("/admin"));

            // admin: create the author through the admin UI, give them the Author role, add a hidden group with a page
            var created = await admin.PostAsync("/admin/users", Form(("__RequestVerificationToken", adminToken), ("userName", "writer"), ("displayName", "Writer"), ("password", "writer-password-1")));
            var userId = created.Headers.Location!.ToString().Split('/').Last();
            int authorRole, contentGroup, homeId;
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Homepage.Data.AppDbContext>();
                authorRole = db.PermissionRoles.Single(r => r.Name == "Author").Id;
                contentGroup = db.Groups.Single(g => g.Name == "content").Id;
                homeId = db.ContentItems.Single(c => c.Slug == "home").Id;
            }
            Assert.Equal(HttpStatusCode.NoContent, (await Htmx(admin, adminToken, "/admin/permissions/user-roles", ("userId", userId), ("roleId", authorRole.ToString()), ("member", "true"))).StatusCode);
            await admin.PostAsync("/admin/groups", Form(("__RequestVerificationToken", adminToken), ("name", "secret"), ("comment", ""), ("valid", "true")));
            int secretGroup;
            using (var scope = app.Services.CreateScope())
                secretGroup = scope.ServiceProvider.GetRequiredService<Homepage.Data.AppDbContext>().Groups.Single(g => g.Name == "secret").Id;
            var secretPage = await admin.PostAsync("/admin/content/new", Form(("__RequestVerificationToken", adminToken), ("title", "Secret"), ("type", "Page"), ("groupId", secretGroup.ToString())));
            var secretTarget = secretPage.Headers.Location!.ToString().Split('/').Last();

            // admin: upload an image and save a menu
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
            var upload = new MultipartFormDataContent
            {
                { new StringContent(adminToken), "__RequestVerificationToken" },
                { new StringContent(contentGroup.ToString()), "groupId" },
                { new ByteArrayContent(png), "files", "dot.png" },
            };
            Assert.Equal(HttpStatusCode.Redirect, (await admin.PostAsync("/admin/media", upload)).StatusCode);
            var badUpload = new MultipartFormDataContent
            {
                { new StringContent(adminToken), "__RequestVerificationToken" },
                { new StringContent(contentGroup.ToString()), "groupId" },
                { new StringContent("<html><script>alert(1)</script>"), "files", "evil.png" },
            };
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/admin/media", badUpload)).StatusCode);
            int mediaId; string stored;
            using (var scope = app.Services.CreateScope())
            {
                var m = scope.ServiceProvider.GetRequiredService<Homepage.Data.AppDbContext>().Media.Single();
                (mediaId, stored) = (m.Id, m.StoredName);
            }
            await admin.PostAsync("/admin/menus/main", Form(("__RequestVerificationToken", adminToken), ("label", "Home"), ("url", "/"), ("label", "Contact us"), ("url", "/contact")));
            Assert.Contains("Contact us", await admin.GetStringAsync("/"));

            // author
            var author = await SignInAsync(app, "writer", "writer-password-1");
            var dash = await author.GetStringAsync("/admin");
            var token = Token(dash);
            Assert.DoesNotContain("Users &amp; permissions", dash);
            Assert.Equal(HttpStatusCode.Forbidden, (await author.GetAsync("/admin/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await author.GetAsync("/admin/themes")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync($"/admin/editor/{secretTarget}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Op(author, token, $"content-{homeId}", """{"op":"insert","type":"heading","parentId":null,"index":0}""")).StatusCode);

            var post = await author.PostAsync("/admin/content/new", Form(("__RequestVerificationToken", token), ("title", "My trip"), ("type", "Post"), ("groupId", contentGroup.ToString())));
            Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
            var target = post.Headers.Location!.ToString().Split('/').Last();
            var editor = await author.GetStringAsync($"/admin/editor/{target}");
            Assert.DoesNotContain("data-type=\"html\"", editor);
            Assert.Contains("data-type=\"image\"", editor);
            Assert.Equal(HttpStatusCode.Forbidden, (await Op(author, token, target, """{"op":"insert","type":"html","parentId":null,"index":0}""")).StatusCode);
            var ins = await Op(author, token, target, """{"op":"insert","type":"image","parentId":null,"index":1}""");
            var imageId = Regex.Match(await ins.Content.ReadAsStringAsync(), "\"newId\":\"([^\"]+)\"").Groups[1].Value;
            Assert.Equal(HttpStatusCode.NoContent, (await Htmx(author, token, $"/admin/editor/{target}/props/{imageId}", ("p.MediaId", mediaId.ToString()), ("p.Alt", "A dot"), ("p.Caption", ""), ("p.Width", "Normal"))).StatusCode);
            var postId = target["content-".Length..];
            Assert.Equal(HttpStatusCode.Forbidden, (await Htmx(author, token, $"/admin/content/{postId}/publish")).StatusCode);

            // admin publishes it; the image is on the public page
            Assert.Equal(HttpStatusCode.NoContent, (await Htmx(admin, adminToken, $"/admin/content/{postId}/publish")).StatusCode);
            var publicPost = await admin.GetStringAsync("/blog/my-trip");
            Assert.Contains($"src=\"/media/{stored}\"", publicPost);
            Assert.Contains("alt=\"A dot\"", publicPost);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/media/{stored}")).StatusCode);

            // a page cannot take an address that a fixed route owns
            var blogPage = await admin.PostAsync("/admin/content/new", Form(("__RequestVerificationToken", adminToken), ("title", "Blog"), ("type", "Page"), ("groupId", contentGroup.ToString())));
            var blogTarget = blogPage.Headers.Location!.ToString().Split('/').Last();
            using (var scope = app.Services.CreateScope())
                Assert.Equal("blog-2", scope.ServiceProvider.GetRequiredService<Homepage.Data.AppDbContext>().ContentItems.Single(c => c.Id == int.Parse(blogTarget.Substring(8))).Slug);

            // revoking the role takes effect on the next request
            Assert.Equal(HttpStatusCode.NoContent, (await Htmx(admin, adminToken, "/admin/permissions/user-roles", ("userId", userId), ("roleId", authorRole.ToString()), ("member", "false"))).StatusCode);
            var after = await author.GetAsync("/admin");
            Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
            Assert.Contains("/admin/login", after.Headers.Location!.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOMEPAGE_ConnectionStrings__Default", null);
            Environment.SetEnvironmentVariable("HOMEPAGE_Paths__Data", null);
        }
    }

    private static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> app, string user, string password)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var token = Token(await client.GetStringAsync("/admin/login"));
        var res = await client.PostAsync("/admin/login", Form(("__RequestVerificationToken", token), ("userName", user), ("password", password)));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return client;
    }

    private static Task<HttpResponseMessage> Op(HttpClient client, string token, string target, string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/admin/editor/{target}/ops") { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Add("RequestVerificationToken", token);
        req.Headers.Add("Accept", "application/json");
        return client.SendAsync(req);
    }

    private static Task<HttpResponseMessage> Htmx(HttpClient client, string token, string url, params (string, string)[] fields)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = Form(fields) };
        req.Headers.Add("RequestVerificationToken", token);
        req.Headers.Add("HX-Request", "true");
        return client.SendAsync(req);
    }

    private static string Token(string html) =>
        Regex.Match(html, "name=\"(?:__RequestVerificationToken|csrf-token)\" (?:value|content)=\"([^\"]+)\"").Groups[1].Value is { Length: > 0 } t
            ? t : throw new InvalidOperationException("No antiforgery token in the page.");

    private static FormUrlEncodedContent Form(params (string, string)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
}

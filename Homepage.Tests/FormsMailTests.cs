using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Homepage.Accounts;
using Homepage.Data;
using Homepage.Forms;
using Homepage.Mail;
using Homepage.Web.Rendering;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homepage.Tests;

public sealed class ShiftedTime(TimeSpan by) : TimeProvider
{
    public TimeSpan By { get; set; } = by;
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + By;
}

/// <summary>Just enough SMTP to receive what the outbox sends (no TLS, no auth): every message's raw text.</summary>
public sealed class FakeSmtp : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FakeSmtp()
    {
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch (OperationCanceledException) { return; }
                _ = Task.Run(() => Serve(client));
            }
        });
    }

    private async Task Serve(System.Net.Sockets.TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
        var writer = new StreamWriter(stream, System.Text.Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 fake ESMTP");
        while (await reader.ReadLineAsync() is { } line)
        {
            var cmd = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
            switch (cmd)
            {
                case "EHLO": case "HELO": await writer.WriteLineAsync("250 fake"); break;
                case "DATA":
                    await writer.WriteLineAsync("354 go on");
                    var sb = new System.Text.StringBuilder();
                    while (await reader.ReadLineAsync() is { } l && l != ".") sb.Append(l.StartsWith("..") ? l[1..] : l).Append("\r\n");
                    Messages.Enqueue(sb.ToString());
                    await writer.WriteLineAsync("250 queued");
                    break;
                case "QUIT": await writer.WriteLineAsync("221 bye"); return;
                default: await writer.WriteLineAsync("250 ok"); break; // MAIL, RCPT, RSET, NOOP
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; } catch (OperationCanceledException) { }
    }
}

public sealed class FixedOptions<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

public class FormsMailUnitTests
{
    private static Form ContactForm() => new()
    {
        Id = 1, Name = "Contact",
        Fields = FormField.ToJson(FormService.CleanFields(
        [
            new FormField { Label = "Name", Type = FieldType.Text, Required = true },
            new FormField { Label = "Email", Type = FieldType.Email, Required = true },
            new FormField { Label = "Topic", Type = FieldType.Select, Options = ["Question", "Other"] },
            new FormField { Label = "Extras", Type = FieldType.Checkboxes, Options = ["A", "B"] },
            new FormField { Label = "Age", Type = FieldType.Number },
            new FormField { Label = "I agree", Type = FieldType.Checkbox },
        ])),
    };

    [Fact]
    public void FieldsGetUniqueKeysFromTheirLabels()
    {
        var f = FormService.CleanFields([new FormField { Label = "Your name" }, new FormField { Label = "Your name" }, new FormField { Label = "Żółć!" }, new FormField { Label = "" }]);
        Assert.Equal(["your_name", "your_name_2", "zolc"], f.Select(x => x.Key));
    }

    [Fact]
    public void AnswersAreCheckedByType()
    {
        var form = ContactForm();
        Dictionary<string, string[]> sent = new() { ["name"] = ["Ann"], ["email"] = ["ann@example.com"], ["topic"] = ["Other"], ["extras"] = ["A", "B"], ["age"] = ["42"], ["i_agree"] = ["true"] };
        var ok = FormService.Check(form, k => sent.GetValueOrDefault(k) ?? []);
        Assert.True(ok.Ok, string.Join(", ", ok.Errors.Values));
        Assert.Equal("Ann", FormService.Text(ok.Data["name"]));
        Assert.Equal("A, B", FormService.Text(ok.Data["extras"]));
        Assert.True(ok.Data["i_agree"]!.GetValue<bool>());

        Dictionary<string, string[]> bad = new() { ["email"] = ["not an email"], ["topic"] = ["Made up"], ["extras"] = ["C"], ["age"] = ["old"] };
        var r = FormService.Check(form, k => bad.GetValueOrDefault(k) ?? []);
        Assert.False(r.Ok);
        Assert.Equal(["age", "email", "extras", "name", "topic"], r.Errors.Keys.Order());
    }

    [Fact]
    public void EmailPlaceholdersAreEscaped()
    {
        var values = new Dictionary<string, string> { ["name"] = "<script>x</script> **bold** [link](javascript:alert(1))", ["confirm.url"] = "https://example.com/hp/mail/confirm/abc" };
        var filled = EmailRenderer.Fill("Hi {{name}} [go]({{confirm.url}}) {{unknown}}", values, markdownContext: true);
        Assert.Contains("https://example.com/hp/mail/confirm/abc", filled);
        Assert.DoesNotContain("**bold**", filled);
        var r = new EmailRenderer(new Homepage.Content.MarkdownRenderer()).Render("Hello {{name}}", "Hi {{name}}\n\n[Confirm]({{confirm.url}})", values, "Site", "https://example.com/u");
        Assert.DoesNotContain("<script>", r.Html);
        Assert.DoesNotContain("href=\"javascript", r.Html);
        Assert.Contains("href=\"https://example.com/hp/mail/confirm/abc\"", r.Html);
        Assert.Contains("https://example.com/u", r.Html); // the unsubscribe footer
        Assert.Contains("Hello <script>x</script>", r.Subject); // subjects are plain text, not HTML
    }

    [Fact]
    public void AddressListsAreChecked()
    {
        Assert.Equal(["a@example.com", "b@example.org"], MailAddresses.CleanList(" a@example.com, b@example.org;a@example.com ", out var bad));
        Assert.Null(bad);
        MailAddresses.CleanList("a@example.com, nope", out bad);
        Assert.Equal("nope", bad);
        Assert.False(MailAddresses.IsValid("a@b"));
        Assert.False(MailAddresses.IsValid("a b@example.com"));
        Assert.True(MailAddresses.IsValid("first.last+tag@sub.example.com"));
    }

    [Fact]
    public void FormGuardCatchesBotsAndReplays()
    {
        var dp = DataProtectionProvider.Create("test");
        var time = new ShiftedTime(TimeSpan.Zero);
        var guard = new FormGuard(dp, time);
        var token = guard.Issue("form:1");
        Assert.Equal(FormGuard.Verdict.TooFast, guard.Check("form:1", token, ""));
        time.By = TimeSpan.FromSeconds(5);
        Assert.Equal(FormGuard.Verdict.Ok, guard.Check("form:1", token, ""));
        Assert.Equal(FormGuard.Verdict.Invalid, guard.Check("form:2", token, "")); // another form's token
        Assert.Equal(FormGuard.Verdict.Trap, guard.Check("form:1", token, "http://spam.example"));
        Assert.Equal(FormGuard.Verdict.Invalid, guard.Check("form:1", "garbage", ""));
        time.By = TimeSpan.FromDays(8);
        Assert.Equal(FormGuard.Verdict.Invalid, guard.Check("form:1", token, ""));
    }
}

/// <summary>Over HTTP: building a form, visitors sending it, the emails it queues, newsletter sign-up, confirm and unsubscribe, sending to a list.</summary>
[Collection("Postgres"), Trait("Category", "Postgres")]
public sealed class FormsMailHttpTests(PostgresFixture pg)
{
    private const string Password = "test-password-123";

    [Fact]
    public async Task FormsListsAndCampaigns()
    {
        var cs = await pg.CreateDatabaseAsync();
        var data = Directory.CreateTempSubdirectory("homepage-test-").FullName;
        Environment.SetEnvironmentVariable("HOMEPAGE_ConnectionStrings__Default", cs);
        Environment.SetEnvironmentVariable("HOMEPAGE_Paths__Data", data);
        try
        {
            await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
            using (var scope = app.Services.CreateScope())
            {
                var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
                Assert.True((await accounts.CreateAsync("admin", "Admin", Password, "test", makeAdmin: true, email: "admin@example.com")).Result.Ok);
                Assert.True((await accounts.CreateAsync("editor", "Editor", Password, "test")).Result.Ok);
            }
            var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
            var token = Token(await client.GetStringAsync("/admin/login"));
            await client.PostAsync("/admin/login", Form(("__RequestVerificationToken", token), ("userName", "admin"), ("password", Password)));
            var dashboard = await client.GetStringAsync("/admin");
            token = Token(dashboard);
            Assert.Contains("href=\"/admin/forms\"", dashboard);
            Assert.Contains("href=\"/admin/mail\"", dashboard);

            // a mailing list with double opt-in, and a form that adds consenting senders to it
            var res = await client.PostAsync("/admin/mail/lists", Form(("__RequestVerificationToken", token), ("name", "News"), ("description", ""), ("doubleOptIn", "true")));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            var listId = res.Headers.Location!.ToString().Split('/').Last();
            const string fields = """[{"label":"Name","type":"Text","required":true},{"label":"Email","type":"Email","required":true},{"label":"Message","type":"Textarea","required":true},{"label":"Send me news","type":"Checkbox"}]""";
            res = await client.PostAsync("/admin/forms", Form(("__RequestVerificationToken", token), ("name", "Contact"), ("enabled", "true"), ("submitLabel", "Send"),
                ("successMessage", "Thanks!"), ("successUrl", ""), ("notifyEmails", "owner@example.com"), ("confirmToSender", "true"), ("mailingListId", listId),
                ("consentField", "Send me news"), ("fields", fields)));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            var formId = res.Headers.Location!.ToString().Split('/').Last();
            // a bad notify address is refused and what was typed is kept
            res = await client.PostAsync($"/admin/forms/{formId}", Form(("__RequestVerificationToken", token), ("name", "Contact"), ("enabled", "true"), ("submitLabel", "Send"),
                ("successMessage", "Thanks!"), ("notifyEmails", "nope"), ("fields", fields)));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Contains("nope", await res.Content.ReadAsStringAsync());

            // on a page: a Form block shows the fields, a hidden trap and a signed token
            res = await client.PostAsync("/admin/content/new", Form(("__RequestVerificationToken", token), ("title", "Contact us"), ("type", "page"), ("groupId", GroupId(dashboard))));
            var pageId = res.Headers.Location!.ToString().Split('-').Last();
            var ins = new HttpRequestMessage(HttpMethod.Post, $"/admin/editor/content-{pageId}/ops")
            {
                Content = new StringContent("""{"op":"insert","type":"form","parentId":null,"index":0}""", System.Text.Encoding.UTF8, "application/json"),
            };
            ins.Headers.Add("RequestVerificationToken", token);
            ins.Headers.Add("Accept", "application/json");
            var blockId = Regex.Match(await (await client.SendAsync(ins)).Content.ReadAsStringAsync(), "\"newId\":\"([^\"]+)\"").Groups[1].Value;
            Assert.Contains($"value=\"{formId}\"", await client.GetStringAsync($"/admin/editor/content-{pageId}/inspector/{blockId}"));
            var props = new HttpRequestMessage(HttpMethod.Post, $"/admin/editor/content-{pageId}/props/{blockId}") { Content = Form(("p.FormId", formId)) };
            props.Headers.Add("RequestVerificationToken", token);
            props.Headers.Add("HX-Request", "true");
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(props)).StatusCode);
            await client.PostAsync($"/admin/content/{pageId}/publish", Form(("__RequestVerificationToken", token)));
            var page = await client.GetStringAsync("/contact-us");
            Assert.Contains($"action=\"/hp/forms/{formId}\"", page);
            Assert.Contains("name=\"message\"", page);
            Assert.Contains("name=\"website\"", page);
            Assert.Contains("/js/hp-forms.js", page);

            // visitors (a fresh client without the admin cookie)
            var visitor = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var old = new FormGuard(app.Services.GetRequiredService<IDataProtectionProvider>(), new ShiftedTime(TimeSpan.FromMinutes(-1)));
            async Task<HttpResponseMessage> Send(string scope, string url, bool json, params (string, string)[] f)
            {
                var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = Form([(FormGuard.TokenField, old.Issue(scope)), (FormGuard.TrapField, ""), .. f]) };
                if (json) r.Headers.Add("Accept", "application/json");
                r.Headers.Referrer = new Uri("http://localhost/contact-us");
                return await visitor.SendAsync(r);
            }
            // too fast (a token from just now)
            var fast = await visitor.PostAsync($"/hp/forms/{formId}", Form((FormGuard.TokenField, Regex.Match(page, "name=\"_hp_t\" value=\"([^\"]+)\"").Groups[1].Value), ("name", "x")));
            Assert.Equal(HttpStatusCode.BadRequest, fast.StatusCode);
            // missing answers come back per field
            res = await Send("form:" + formId, $"/hp/forms/{formId}", true, ("name", "Ann"), ("email", "nope"));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var errors = JsonNode.Parse(await res.Content.ReadAsStringAsync())!["errors"]!.AsObject();
            Assert.True(errors.ContainsKey("email") && errors.ContainsKey("message"));
            // a bot fills the trap: looks fine, nothing kept
            var trap = new HttpRequestMessage(HttpMethod.Post, $"/hp/forms/{formId}") { Content = Form((FormGuard.TokenField, old.Issue("form:" + formId)), (FormGuard.TrapField, "spam"), ("name", "Bot")) };
            trap.Headers.Add("Accept", "application/json");
            Assert.Equal(HttpStatusCode.OK, (await visitor.SendAsync(trap)).StatusCode);
            // a person
            res = await Send("form:" + formId, $"/hp/forms/{formId}", true, ("name", "Ann <b>"), ("email", "ann@example.com"), ("message", "Hello\nthere"), ("send_me_news", "true"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("Thanks!", JsonNode.Parse(await res.Content.ReadAsStringAsync())!["message"]!.GetValue<string>());
            // without script: a page
            res = await Send("form:" + formId, $"/hp/forms/{formId}", false, ("name", "Bo"), ("email", "bo@example.com"), ("message", "Hi"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Contains("Thanks!", await res.Content.ReadAsStringAsync());

            await using (var db = PostgresFixture.Context(cs))
            {
                var subs = await db.FormSubmissions.Where(s => s.FormId == int.Parse(formId)).ToListAsync();
                Assert.Equal(2, subs.Count);
                Assert.All(subs, s => Assert.Equal("/contact-us", s.PageUrl));
                var outbox = await db.Outbox.OrderBy(m => m.Id).ToListAsync();
                Assert.Equal(2, outbox.Count(m => m.Kind == SystemEmails.FormNotification && m.To == "owner@example.com"));
                Assert.Contains(outbox, m => m.Kind == SystemEmails.FormNotification && m.ReplyTo == "ann@example.com" && m.Html.Contains("Ann &lt;b&gt;"));
                Assert.Contains(outbox, m => m.Kind == SystemEmails.FormConfirmation && m.To == "bo@example.com");
                // Ann agreed: she is pending on the list and got the confirm email; Bo did not tick it
                var confirm = Assert.Single(outbox, m => m.Kind == SystemEmails.ListConfirm);
                Assert.Equal("ann@example.com", confirm.To);
                Assert.Single(await db.Subscribers.ToListAsync(), s => s.Email == "ann@example.com" && s.Status == SubscriberStatus.Pending);
            }

            // the admin sees two new answers, reads one, exports them
            var forms = await client.GetStringAsync("/admin/forms");
            Assert.Contains("2 new", forms);
            Assert.Contains("class=\"nav-count\" title=\"New answers\">2<", forms);
            var answers = await client.GetStringAsync($"/admin/forms/{formId}/submissions");
            var sid = Regex.Matches(answers, $"/admin/forms/{formId}/submissions/(\\d+)").Last().Groups[1].Value; // newest first: Ann's is last
            var detail = await client.GetStringAsync($"/admin/forms/{formId}/submissions/{sid}");
            Assert.Contains("<dd>Hello&#xA;there</dd>", detail); // the line break is kept (white-space: pre-wrap)
            Assert.Contains("1 new", await client.GetStringAsync("/admin/forms"));
            var csv = await client.GetStringAsync($"/admin/forms/{formId}/export");
            Assert.Contains("received,page,Name,Email,Message,Send me news", csv);
            Assert.Contains("\"Hello\nthere\"", csv);

            // newsletter: subscribe (the same answer whether known or not), confirm by button, one-click unsubscribe
            res = await Send("list:" + listId, $"/hp/mail/subscribe/{listId}", true, ("email", "cy@example.com"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Contains("check your inbox", await res.Content.ReadAsStringAsync());
            string cyToken;
            await using (var db = PostgresFixture.Context(cs)) cyToken = (await db.Subscribers.SingleAsync(s => s.Email == "cy@example.com")).Token;
            var confirmPage = await visitor.GetStringAsync($"/hp/mail/confirm/{cyToken}");
            Assert.Contains("Yes, subscribe me", confirmPage); // a GET (a link scanner) does not confirm
            await using (var db = PostgresFixture.Context(cs)) Assert.Equal(SubscriberStatus.Pending, (await db.Subscribers.SingleAsync(s => s.Email == "cy@example.com")).Status);
            res = await visitor.PostAsync($"/hp/mail/confirm/{cyToken}", Form());
            Assert.Contains("You are subscribed", await res.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/hp/mail/confirm/nosuchtoken12345")).StatusCode);
            // signing up again with a known address answers exactly like a new one
            var again = await (await Send("list:" + listId, $"/hp/mail/subscribe/{listId}", true, ("email", "cy@example.com"))).Content.ReadAsStringAsync();
            var fresh = await (await Send("list:" + listId, $"/hp/mail/subscribe/{listId}", true, ("email", "dee@example.com"))).Content.ReadAsStringAsync();
            Assert.Equal(fresh, again);

            // send to the list: only subscribed people, each with their own unsubscribe link
            res = await client.PostAsync("/admin/mail/send", Form(("__RequestVerificationToken", token), ("listId", listId), ("subject", "Hello {{name}}"), ("body", "News for {{email}}"), ("action", "send")));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            await using (var db = PostgresFixture.Context(cs))
            {
                var campaign = Assert.Single(await db.Outbox.Where(m => m.Kind == "campaign").ToListAsync());
                Assert.Equal("cy@example.com", campaign.To);
                Assert.Equal($"http://localhost/hp/mail/unsubscribe/{cyToken}", campaign.UnsubscribeUrl);
                Assert.Contains("News for cy@example.com", campaign.Text);
            }
            Assert.Contains("0 of 1 sent", await client.GetStringAsync("/admin/mail/sent")); // no SMTP server configured: it waits in the outbox

            // the outbox worker sends everything to an SMTP server
            await using (var smtp = new FakeSmtp())
            {
                var options = new MailOptions { Host = "127.0.0.1", Port = smtp.Port, Security = "None", FromAddress = "site@example.com", FromName = "My site" };
                var worker = new OutboxWorker(new TestDbFactory(cs), new FixedOptions<MailOptions>(options), TimeProvider.System,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<OutboxWorker>.Instance);
                var sentNow = await worker.RunOnceAsync(default);
                await using var db = PostgresFixture.Context(cs);
                Assert.Equal(await db.Outbox.CountAsync(), sentNow);
                Assert.All(await db.Outbox.ToListAsync(), m => Assert.Equal(OutboxStatus.Sent, m.Status));
                Assert.Equal("", (await db.Outbox.SingleAsync(m => m.Kind == "campaign")).Html); // one copy of a campaign's text is enough
                var mails = smtp.Messages.Select(raw => MimeKit.MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw)))).ToList();
                Assert.Equal(sentNow, mails.Count);
                Assert.All(mails, m => Assert.Equal("\"My site\" <site@example.com>", m.From.ToString()));
                var note = mails.First(m => m.To.ToString() == "owner@example.com" && m.ReplyTo.ToString() == "ann@example.com");
                Assert.Contains("Ann <b>", note.TextBody);
                Assert.Contains("Ann &lt;b&gt;", note.HtmlBody);
                var news = Assert.Single(mails, m => m.To.ToString() == "cy@example.com" && m.Subject.Trim() == "Hello");
                Assert.Equal($"<http://localhost/hp/mail/unsubscribe/{cyToken}>", news.Headers["List-Unsubscribe"]);
                Assert.Equal("List-Unsubscribe=One-Click", news.Headers["List-Unsubscribe-Post"]);
                Assert.Contains($"http://localhost/hp/mail/unsubscribe/{cyToken}", news.HtmlBody);
            }
            var oneClick = await visitor.PostAsync($"/hp/mail/unsubscribe/{cyToken}", Form(("List-Unsubscribe", "One-Click")));
            Assert.Equal(HttpStatusCode.OK, oneClick.StatusCode);
            await using (var db = PostgresFixture.Context(cs)) Assert.Equal(SubscriberStatus.Unsubscribed, (await db.Subscribers.SingleAsync(s => s.Email == "cy@example.com")).Status);
            res = await client.PostAsync("/admin/mail/send", Form(("__RequestVerificationToken", token), ("listId", listId), ("subject", "Again"), ("body", "x"), ("action", "send")));
            Assert.Contains("Nobody is subscribed", await res.Content.ReadAsStringAsync());

            // system emails: edit, turn off, reset
            await client.PostAsync($"/admin/mail/system/{SystemEmails.PasswordChanged}", Form(("__RequestVerificationToken", token), ("subject", "Changed!"), ("body", "x")));
            Assert.Contains("Changed!", await client.GetStringAsync("/admin/mail/system"));
            Assert.Contains("badge-off\">off", await client.GetStringAsync("/admin/mail/system"));

            // an editor (content: rw) has no forms or mail permissions
            await using (var db = PostgresFixture.Context(cs))
            {
                var editorId = (await db.Users.SingleAsync(u => u.UserName == "editor")).Id;
                var contentGroup = (await db.Groups.SingleAsync(g => g.Name == Homepage.Permissions.SystemGroups.Content)).Id;
                db.UserGroups.Add(new UserGroup { UserId = editorId, GroupId = contentGroup, Permission = Homepage.Permissions.PermNames.ToKey(Homepage.Permissions.Perm.Rw) });
                await db.SaveChangesAsync();
            }
            var other = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
            var t2 = Token(await other.GetStringAsync("/admin/login"));
            await other.PostAsync("/admin/login", Form(("__RequestVerificationToken", t2), ("userName", "editor"), ("password", Password)));
            var dash2 = await other.GetStringAsync("/admin");
            Assert.DoesNotContain("href=\"/admin/forms\"", dash2);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/admin/forms")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/admin/forms/{formId}/export")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/admin/mail")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/admin/mail/lists/{listId}/export")).StatusCode);

            // with forms: ro and mail: ro: reading works; exporting, editing and sending are neither offered nor allowed
            using (var scope = app.Services.CreateScope())
            {
                var sp = scope.ServiceProvider;
                await using var db = PostgresFixture.Context(cs);
                var editorId = (await db.Users.SingleAsync(u => u.UserName == "editor")).Id;
                var adminId = (await db.Users.SingleAsync(u => u.UserName == "admin")).Id;
                var adminPerms = await sp.GetRequiredService<Homepage.Permissions.IPermissionService>().GetAsync(adminId);
                foreach (var g in new[] { Homepage.Permissions.SystemGroups.Forms, Homepage.Permissions.SystemGroups.Mail })
                    await sp.GetRequiredService<Homepage.Permissions.PermissionAdminService>().SetUserGroupAsync(adminPerms, editorId,
                        (await db.Groups.SingleAsync(x => x.Name == g)).Id, Homepage.Permissions.Perm.Ro, true, "test");
            }
            var roAnswers = await other.GetStringAsync($"/admin/forms/{formId}/submissions");
            Assert.DoesNotContain("/export", roAnswers);
            Assert.DoesNotContain($"href=\"/admin/forms/{formId}\"", roAnswers); // no "Edit form"
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/admin/forms/{formId}/export")).StatusCode);
            var roMail = await other.GetStringAsync("/admin/mail");
            Assert.DoesNotContain("href=\"/admin/mail/send\"", roMail);
            Assert.DoesNotContain("Send a test email", roMail);
            Assert.DoesNotContain("href=\"/admin/mail/system/", await other.GetStringAsync("/admin/mail/system"));
            Assert.DoesNotContain("/export", await other.GetStringAsync($"/admin/mail/lists/{listId}"));
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/admin/mail/send")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOMEPAGE_ConnectionStrings__Default", null);
            Environment.SetEnvironmentVariable("HOMEPAGE_Paths__Data", null);
        }
    }

    private static string GroupId(string dashboard)
    {
        var gm = Regex.Match(dashboard, "name=\"groupId\"(?:\\s+value=\"(\\d+)\"|>\\s*<option value=\"(\\d+)\")");
        return gm.Groups[1].Success ? gm.Groups[1].Value : gm.Groups[2].Value;
    }

    private static string Token(string html) =>
        Regex.Match(html, "name=\"(?:__RequestVerificationToken|csrf-token)\" (?:value|content)=\"([^\"]+)\"").Groups[1].Value is { Length: > 0 } t
            ? t : throw new InvalidOperationException("No antiforgery token in the page.");

    private static FormUrlEncodedContent Form(params (string, string)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
}

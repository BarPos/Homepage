using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Homepage.Content;
using Homepage.Forms;
using Homepage.Mail;
using Homepage.Web.Rendering;
using Homepage.Web.Site;

namespace Homepage.Web.Endpoints;

/// <summary>
/// Public form posts: forms (/hp/forms/{id}), newsletter sign-ups (/hp/mail/subscribe/{list}), and the confirm and
/// unsubscribe links in emails. The script on the page sends with Accept: application/json and shows the answer in
/// place; without script the browser gets a page.
/// </summary>
public static partial class FormsEndpoints
{
    private const long MaxBody = 64 * 1024;

    [GeneratedRegex(@"[^A-Za-z0-9/_\-.%~]")]
    private static partial Regex UnsafePathChars();

    [GeneratedRegex(@"^(form|list):[0-9]{1,9}$")]
    private static partial Regex TokenScope();

    private static bool WantsJson(HttpRequest req) => req.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);

    /// <summary>The page a form was sent from: the path of a same-site Referer (safe characters only), for the admin and emails.</summary>
    private static string FromPage(HttpRequest req)
    {
        if (!Uri.TryCreate(req.Headers.Referer.ToString(), UriKind.Absolute, out var r) || !string.Equals(r.Host, req.Host.Host, StringComparison.OrdinalIgnoreCase)) return "";
        var path = UnsafePathChars().Replace(r.AbsolutePath, "");
        return path.Length > 300 ? path[..300] : path;
    }

    private static IResult Answer(HttpRequest req, RenderContext ctx, bool ok, string message, Dictionary<string, string>? errors = null, string? redirect = null,
        string heading = "")
    {
        if (WantsJson(req))
            return Results.Json(new { ok, message, errors = errors ?? [], redirect }, statusCode: ok ? 200 : 400);
        if (ok && redirect is not null) return Results.Redirect(redirect);
        var items = errors?.Values.Distinct().ToList() ?? [];
        return Html.Render<MessagePage>(new
        {
            Ctx = ctx, Heading = heading.Length > 0 ? heading : ok ? "Thank you" : "Please check the form",
            Text = message, Items = items, ButtonText = ok ? "Go to the home page" : "Back to the form",
            ButtonUrl = ok ? "/" : (req.Headers.Referer.ToString() is { Length: > 0 } back && back.StartsWith('/') ? back : FromPage(req) is { Length: > 0 } p ? p : "/"),
        }, ok ? 200 : 400);
    }

    /// <summary>The posted form, at most <see cref="MaxBody"/> bytes (also when the client sends no Content-Length); null if it is not one.</summary>
    private static async Task<IFormCollection?> ReadAsync(HttpRequest req, CancellationToken ct)
    {
        if (req.ContentLength is > MaxBody || !req.HasFormContentType) return null;
        if (req.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxBody;
        try { return await req.ReadFormAsync(new Microsoft.AspNetCore.Http.Features.FormOptions { ValueLengthLimit = (int)MaxBody, ValueCountLimit = 200 }, ct); }
        catch (Exception ex) when (ex is BadHttpRequestException or InvalidDataException) { return null; }
    }

    public static void MapForms(this WebApplication app)
    {
        app.MapPost("/hp/forms/{id:int}", async (int id, HttpRequest req, FormService forms, MailService mail, FormGuard guard, RenderContextFactory ctxf,
            CancellationToken ct) =>
        {
            var ctx = await ctxf.CreateAsync(ct: ct);
            var form = await forms.GetAsync(id, ct);
            if (form is null) return Answer(req, ctx, false, "This form no longer exists.");
            if (!form.Enabled) return Answer(req, ctx, false, "This form is closed.");
            if (await ReadAsync(req, ct) is not { } data) return Answer(req, ctx, false, "The form could not be read.");
            switch (guard.Check("form:" + id, data[FormGuard.TokenField], data[FormGuard.TrapField]))
            {
                case FormGuard.Verdict.Trap: return Answer(req, ctx, true, form.SuccessMessage); // looks fine to the bot; nothing is kept
                case FormGuard.Verdict.TooFast: return Answer(req, ctx, false, "That was quick! Please wait a moment and send it again.");
                case FormGuard.Verdict.Invalid: return Answer(req, ctx, false, "The form has expired. Reload the page and send it again.");
            }
            var check = FormService.Check(form, name => data[name].Select(v => v ?? "").ToList());
            if (!check.Ok) return Answer(req, ctx, false, "Some answers need another look.", check.Errors);

            var page = FromPage(req);
            await forms.StoreAsync(form, check.Data, page, ct);

            var baseUrl = SiteEndpoints.BaseUrl(ctx.Site.BaseUrl, req);
            var fields = FormField.Parse(form.Fields);
            var answers = EmailRenderer.AnswersMarkdown(fields.Select(f => (f.Label, FormService.Text(check.Data.GetValueOrDefault(f.Key)))));
            var senderEmail = fields.Where(f => f.Type == FieldType.Email).Select(f => FormService.Text(check.Data.GetValueOrDefault(f.Key))).FirstOrDefault(MailAddresses.IsValid) ?? "";
            var senderName = fields.Where(f => f.Type == FieldType.Text && f.Key.Contains("name", StringComparison.Ordinal))
                .Select(f => FormService.Text(check.Data.GetValueOrDefault(f.Key))).FirstOrDefault(n => n.Length > 0) ?? "";
            var values = new Dictionary<string, string>
            {
                ["form.name"] = form.Name, ["answers"] = answers, ["admin.url"] = $"{baseUrl}/admin/forms/{form.Id}/submissions",
                ["page.url"] = baseUrl + (page.Length > 0 ? page : "/"),
            };
            foreach (var to in MailAddresses.CleanList(form.NotifyEmails, out _))
                await mail.SendSystemAsync(SystemEmails.FormNotification, to, "", new Dictionary<string, string>(values), ctx.Site.Title, baseUrl, replyTo: senderEmail, ct: ct);
            if (form.ConfirmToSender && senderEmail.Length > 0)
                await mail.SendSystemAsync(SystemEmails.FormConfirmation, senderEmail, senderName, new Dictionary<string, string>(values), ctx.Site.Title, baseUrl, ct: ct);
            if (form.MailingListId is { } listId && form.ConsentFieldKey is { } consent && check.Data.GetValueOrDefault(consent) is JsonValue v && v.TryGetValue<bool>(out var agreed) && agreed
                && senderEmail.Length > 0)
            {
                try { await mail.SubscribeAsync(listId, senderEmail, senderName, "Form: " + form.Name, ctx.Site.Title, baseUrl, ct: ct); }
                catch (Exception ex) when (ex is Homepage.Blocks.BlockException or NotFoundException) { /* the submission itself is kept */ }
            }

            var redirect = string.IsNullOrWhiteSpace(form.SuccessUrl) ? null : ctx.Href(form.SuccessUrl);
            return Answer(req, ctx, true, form.SuccessMessage, redirect: redirect);
        }).RequireRateLimiting("forms").DisableAntiforgery();

        // A fresh anti-spam token: the one in the page may come from the page cache.
        app.MapGet("/hp/forms/token", (string? scope, FormGuard guard, HttpResponse res) =>
        {
            if (scope is null || !TokenScope().IsMatch(scope)) return Results.BadRequest();
            res.Headers.CacheControl = "no-store";
            return Results.Json(new { token = guard.Issue(scope) });
        }).RequireRateLimiting("forms");

        app.MapPost("/hp/mail/subscribe/{listId:int}", async (int listId, HttpRequest req, MailService mail, FormGuard guard, RenderContextFactory ctxf,
            CancellationToken ct) =>
        {
            var ctx = await ctxf.CreateAsync(ct: ct);
            if (await ReadAsync(req, ct) is not { } data) return Answer(req, ctx, false, "The form could not be read.");
            switch (guard.Check("list:" + listId, data[FormGuard.TokenField], data[FormGuard.TrapField]))
            {
                case FormGuard.Verdict.Trap: return Answer(req, ctx, true, "Almost done: check your inbox and click the link to confirm.");
                case FormGuard.Verdict.TooFast: return Answer(req, ctx, false, "That was quick! Please wait a moment and try again.");
                case FormGuard.Verdict.Invalid: return Answer(req, ctx, false, "The form has expired. Reload the page and try again.");
            }
            var email = data["email"].ToString();
            if (!MailAddresses.IsValid(email.Trim())) return Answer(req, ctx, false, "Enter an email address.", new() { ["email"] = "Enter an email address." });
            try
            {
                var r = await mail.SubscribeAsync(listId, email, data["name"].ToString(), "Sign-up block", ctx.Site.Title, SiteEndpoints.BaseUrl(ctx.Site.BaseUrl, req), ct: ct);
                // The answer depends only on the list, never on the address: nobody learns who is on it.
                return Answer(req, ctx, true, r.DoubleOptIn ? "Almost done: check your inbox and click the link to confirm." : "Thank you! You are subscribed.",
                    heading: "Thank you");
            }
            catch (NotFoundException) { return Answer(req, ctx, false, "This sign-up is no longer available."); }
        }).RequireRateLimiting("forms").DisableAntiforgery();

        // Links in emails open a page with a button: link scanners in mail systems open links too, but do not press buttons.
        app.MapGet("/hp/mail/confirm/{token}", async (string token, MailService mail, RenderContextFactory ctxf, CancellationToken ct) =>
        {
            var ctx = await ctxf.CreateAsync(ct: ct);
            var list = token.Length is > 10 and <= 64 ? await mail.ListOfTokenAsync(token, ct) : null;
            return list is null
                ? Html.Render<MessagePage>(new { Ctx = ctx, Heading = "Link not found", Text = "This confirmation link is not valid any more." }, 404)
                : Html.Render<MessagePage>(new { Ctx = ctx, Heading = "Confirm your subscription", Text = $"Get “{list.Name}” by email?", PostUrl = $"/hp/mail/confirm/{token}", PostLabel = "Yes, subscribe me" });
        });
        app.MapPost("/hp/mail/confirm/{token}", async (string token, HttpRequest req, MailService mail, RenderContextFactory ctxf, CancellationToken ct) =>
        {
            var ctx = await ctxf.CreateAsync(ct: ct);
            var list = token.Length is > 10 and <= 64 ? await mail.ConfirmAsync(token, ctx.Site.Title, SiteEndpoints.BaseUrl(ctx.Site.BaseUrl, req), ct) : null;
            return list is null
                ? Html.Render<MessagePage>(new { Ctx = ctx, Heading = "Link not found", Text = "This confirmation link is not valid any more." }, 404)
                : Html.Render<MessagePage>(new { Ctx = ctx, Heading = "You are subscribed", Text = $"Thank you! You will get “{list.Name}” by email." });
        }).RequireRateLimiting("forms").DisableAntiforgery();

        app.MapGet("/hp/mail/unsubscribe/{token}", async (string token, MailService mail, RenderContextFactory ctxf, CancellationToken ct) =>
        {
            var ctx = await ctxf.CreateAsync(ct: ct);
            var list = token.Length is > 10 and <= 64 ? await mail.ListOfTokenAsync(token, ct) : null;
            return list is null
                ? Html.Render<MessagePage>(new { Ctx = ctx, Heading = "Link not found", Text = "This unsubscribe link is not valid any more." }, 404)
                : Html.Render<MessagePage>(new { Ctx = ctx, Heading = "Unsubscribe", Text = $"Stop getting “{list.Name}” by email?", PostUrl = $"/hp/mail/unsubscribe/{token}", PostLabel = "Unsubscribe" });
        });
        // Also the one-click unsubscribe of mail apps (RFC 8058: POST with List-Unsubscribe=One-Click).
        app.MapPost("/hp/mail/unsubscribe/{token}", async (string token, HttpRequest req, MailService mail, RenderContextFactory ctxf, CancellationToken ct) =>
        {
            var list = token.Length is > 10 and <= 64 ? await mail.UnsubscribeAsync(token, ct) : null;
            if (req.HasFormContentType && (await req.ReadFormAsync(ct))["List-Unsubscribe"] == "One-Click") return Results.Ok();
            var ctx = await ctxf.CreateAsync(ct: ct);
            return list is null
                ? Html.Render<MessagePage>(new { Ctx = ctx, Heading = "Link not found", Text = "This unsubscribe link is not valid any more." }, 404)
                : Html.Render<MessagePage>(new { Ctx = ctx, Heading = "You are unsubscribed", Text = $"You will not get “{list.Name}” any more." });
        }).RequireRateLimiting("forms").DisableAntiforgery();
    }
}

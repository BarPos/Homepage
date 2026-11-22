using System.Globalization;
using System.Text;
using Homepage.Blocks;
using Homepage.Common;
using Homepage.Content;
using Homepage.Data;
using Homepage.Mail;
using Homepage.Permissions;
using Homepage.Web.Admin;
using Homepage.Web.Rendering;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;

namespace Homepage.Web.Endpoints;

/// <summary>/admin/mail: the SMTP status and outbox, mailing lists, sending to a list, templates and system emails (mail: ro to look, rw to change and send).</summary>
public static class MailAdminEndpoints
{
    private static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static async Task<(string Title, string BaseUrl)> Site(IDbContextFactory<AppDbContext> dbf, HttpRequest req)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var s = await SiteSettings.LoadAsync(db);
        return (s.Title, SiteEndpoints.BaseUrl(s.BaseUrl, req));
    }

    /// <summary>An account system email (created, password changed, two-factor on/off) to the user, if they have an address.</summary>
    internal static async Task AccountEmailAsync(HttpContext http, string key, User? user, string? state = null)
    {
        if (user is null || !MailAddresses.IsValid(user.Email)) return;
        var services = http.RequestServices;
        var (title, baseUrl) = await Site(services.GetRequiredService<IDbContextFactory<AppDbContext>>(), http.Request);
        var values = new Dictionary<string, string>
        {
            ["user.name"] = user.DisplayName.Length > 0 ? user.DisplayName : user.UserName ?? "", ["user.login"] = user.UserName ?? "",
            ["signin.url"] = baseUrl + "/admin/login",
        };
        if (state is not null) values["state"] = state;
        await services.GetRequiredService<MailService>().SendSystemAsync(key, user.Email!, user.DisplayName, values, title, baseUrl);
    }

    private static bool CanWrite(HttpContext http) => AdminContext.Get(http).Perms.HasSystem(SystemGroups.Mail, Perm.Rw);

    public static void MapMailAdmin(this RouteGroupBuilder admin)
    {
        // ---------------------------------------------------------------- overview
        admin.MapGet("/mail", async (HttpContext http, MailService mail, UserManager<User> users) =>
        {
            var a = AdminContext.Get(http);
            MailService.RequireRead(a.Perms);
            var me = await users.FindByIdAsync(a.UserId.ToString(CultureInfo.InvariantCulture));
            var recent = await mail.OutboxAsync(a.Perms, null, 1, 10);
            var (message, error) = FlashCookie.Take(http);
            return Html.Render<MailPage>(new
            {
                mail.Options,
                Pending = (await mail.OutboxAsync(a.Perms, OutboxStatus.Pending, 1, 1)).Total,
                Sent = (await mail.OutboxAsync(a.Perms, OutboxStatus.Sent, 1, 1)).Total,
                Failed = (await mail.OutboxAsync(a.Perms, OutboxStatus.Failed, 1, 1)).Total,
                Recent = recent.Items, MyEmail = me?.Email ?? "", CanWrite = CanWrite(http), Message = message, Error = error,
            });
        });

        admin.MapPost("/mail/test", async (HttpContext http, HttpRequest req, MailService mail, UserManager<User> users, IDbContextFactory<AppDbContext> dbf) =>
        {
            var a = AdminContext.Get(http);
            var me = await users.FindByIdAsync(a.UserId.ToString(CultureInfo.InvariantCulture));
            var (title, baseUrl) = await Site(dbf, req);
            try
            {
                await mail.SendTestAsync(a.Perms, "Email works", $"Hello!\n\nThis is a test email from **{EmailRenderer.EscapeMarkdown(title)}**. If you can read it, sending email works.", me?.Email ?? "", title, baseUrl);
            }
            catch (BlockException ex) { return FlashCookie.Redirect(http, "/admin/mail", ex.Message, error: true); }
            return FlashCookie.Redirect(http, "/admin/mail", $"Test email queued for {me!.Email}. It should arrive within a minute; if not, see the outbox below.");
        });

        admin.MapPost("/mail/retry", async (HttpContext http, MailService mail) =>
        {
            var a = AdminContext.Get(http);
            var n = await mail.RetryFailedAsync(a.Perms, a.Actor);
            return FlashCookie.Redirect(http, "/admin/mail", n == 1 ? "1 email will be tried again." : $"{n} emails will be tried again.");
        });

        // ---------------------------------------------------------------- lists
        admin.MapGet("/mail/lists", async (HttpContext http, MailService mail) =>
        {
            MailService.RequireRead(AdminContext.Get(http).Perms);
            var (message, error) = FlashCookie.Take(http);
            return Html.Render<MailListsPage>(new { Lists = await mail.ListsAsync(), Counts = await mail.ListCountsAsync(), CanWrite = CanWrite(http), Message = message, Error = error });
        });

        admin.MapPost("/mail/lists", async (HttpContext http, MailService mail) =>
        {
            var a = AdminContext.Get(http);
            var f = await http.Request.ReadFormAsync();
            try
            {
                var list = await mail.SaveListAsync(a.Perms, null, f["name"].ToString(), f["description"].ToString(), f["doubleOptIn"] == "true", a.Actor);
                return FlashCookie.Redirect(http, $"/admin/mail/lists/{list.Id}", "List created. Add a Newsletter sign-up block to a page so visitors can join.");
            }
            catch (BlockException ex) { return FlashCookie.Redirect(http, "/admin/mail/lists", ex.Message, error: true); }
        });

        admin.MapGet("/mail/lists/{id:int}", async (int id, HttpContext http, HttpRequest req, MailService mail) =>
        {
            var a = AdminContext.Get(http);
            MailService.RequireRead(a.Perms);
            var list = (await mail.ListsAsync()).FirstOrDefault(l => l.Id == id) ?? throw new NotFoundException();
            var url = ListUrl.From(req, "q", "status", "size", "page");
            SubscriberStatus? status = Enum.TryParse<SubscriberStatus>(url["status"], out var st) ? st : null;
            var result = await mail.SubscribersAsync(a.Perms, id, url["q"], status, Int(url["page"]), Int(url["size"]));
            var (message, error) = FlashCookie.Take(http);
            return Html.Render<MailListPage>(new { List = list, Result = result, Url = url, CanWrite = CanWrite(http), Message = message, Error = error });
        });

        admin.MapPost("/mail/lists/{id:int}", async (int id, HttpContext http, MailService mail, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            var f = await http.Request.ReadFormAsync();
            try { await mail.SaveListAsync(a.Perms, id, f["name"].ToString(), f["description"].ToString(), f["doubleOptIn"] == "true", a.Actor); }
            catch (BlockException ex) { return FlashCookie.Redirect(http, $"/admin/mail/lists/{id}", ex.Message, error: true); }
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            return FlashCookie.Redirect(http, $"/admin/mail/lists/{id}", "Saved.");
        });

        admin.MapPost("/mail/lists/{id:int}/delete", async (int id, HttpContext http, MailService mail, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            try { await mail.DeleteListAsync(a.Perms, id, a.Actor); }
            catch (ConflictException ex) { return FlashCookie.Redirect(http, $"/admin/mail/lists/{id}", ex.Message, error: true); }
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            return FlashCookie.Redirect(http, "/admin/mail/lists", "List deleted.");
        });

        admin.MapPost("/mail/lists/{id:int}/subscribers", async (int id, HttpContext http, HttpRequest req, MailService mail, IDbContextFactory<AppDbContext> dbf) =>
        {
            var a = AdminContext.Get(http);
            MailService.RequireWrite(a.Perms);
            var f = await http.Request.ReadFormAsync();
            var (title, baseUrl) = await Site(dbf, req);
            try
            {
                var r = await mail.SubscribeAsync(id, f["email"].ToString(), f["name"].ToString(), "Added by " + a.Actor, title, baseUrl, confirmed: f["confirmed"] == "true");
                return FlashCookie.Redirect(http, $"/admin/mail/lists/{id}",
                    r.AlreadySubscribed ? $"{r.Subscriber.Email} is already on the list."
                    : r.NeedsConfirmation ? $"{r.Subscriber.Email} was sent a confirmation email and joins once they click the link."
                    : $"{r.Subscriber.Email} is subscribed.");
            }
            catch (BlockException ex) { return FlashCookie.Redirect(http, $"/admin/mail/lists/{id}", ex.Message, error: true); }
        });

        admin.MapPost("/mail/lists/{id:int}/subscribers/{sid:long}", async (int id, long sid, HttpContext http, MailService mail) =>
        {
            var a = AdminContext.Get(http);
            var f = await http.Request.ReadFormAsync();
            switch (f["action"].ToString())
            {
                case "unsubscribe":
                    await mail.SetSubscriberStatusAsync(a.Perms, id, sid, SubscriberStatus.Unsubscribed, a.Actor);
                    return FlashCookie.Redirect(http, $"/admin/mail/lists/{id}", "Unsubscribed.");
                case "delete":
                    await mail.DeleteSubscriberAsync(a.Perms, id, sid, a.Actor);
                    return FlashCookie.Redirect(http, $"/admin/mail/lists/{id}", "Subscriber deleted.");
                default: return Results.BadRequest();
            }
        });

        admin.MapGet("/mail/lists/{id:int}/export", async (int id, HttpContext http, MailService mail) =>
        {
            var a = AdminContext.Get(http);
            var list = (await mail.ListsAsync()).FirstOrDefault(l => l.Id == id) ?? throw new NotFoundException();
            var rows = await mail.AllSubscribersAsync(a.Perms, id, a.Actor);
            var sb = new StringBuilder("email,name,status,subscribed,source\n");
            foreach (var s in rows)
                sb.Append(AdminEndpoints.Csv(s.Email)).Append(',').Append(AdminEndpoints.Csv(s.Name)).Append(',').Append(s.Status.ToString().ToLowerInvariant()).Append(',')
                  .Append((s.ConfirmedAtUtc ?? s.CreatedAtUtc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',').Append(AdminEndpoints.Csv(s.Source)).Append('\n');
            var name = new string(list.Name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv; charset=utf-8",
                $"list-{(name.Length > 0 ? name : id.ToString(CultureInfo.InvariantCulture))}-{DateTime.UtcNow:yyyy-MM-dd}.csv");
        });

        // ---------------------------------------------------------------- sending to a list
        admin.MapGet("/mail/send", async (HttpContext http, MailService mail, int? list, int? template) =>
        {
            MailService.RequireWrite(AdminContext.Get(http).Perms);
            var templates = (await mail.TemplatesAsync()).Where(t => t.Key is null).ToList();
            var t = templates.FirstOrDefault(x => x.Id == template);
            var (message, error) = FlashCookie.Take(http);
            return Html.Render<MailSendPage>(new
            {
                Lists = await mail.ListsAsync(), Counts = await mail.ListCountsAsync(), Templates = templates, ListId = list,
                Subject = t?.Subject ?? "", Body = t?.Body ?? "", MailReady = mail.Options.Configured, Message = message, Error = error,
            });
        });

        admin.MapPost("/mail/send", async (HttpContext http, HttpRequest req, MailService mail, UserManager<User> users, IDbContextFactory<AppDbContext> dbf) =>
        {
            var a = AdminContext.Get(http);
            var f = await http.Request.ReadFormAsync();
            var listId = Int(f["listId"]) ?? 0;
            string subject = f["subject"].ToString(), body = f["body"].ToString();
            var (title, baseUrl) = await Site(dbf, req);
            async Task<IResult> Again(string? message, string? error) => Html.Render<MailSendPage>(new
            {
                Lists = await mail.ListsAsync(), Counts = await mail.ListCountsAsync(), Templates = (await mail.TemplatesAsync()).Where(t => t.Key is null).ToList(),
                ListId = listId, Subject = subject, Body = body, MailReady = mail.Options.Configured, Message = message, Error = error,
            });
            try
            {
                if (f["action"] == "test")
                {
                    var me = await users.FindByIdAsync(a.UserId.ToString(CultureInfo.InvariantCulture));
                    if (subject.Trim().Length == 0) throw new BlockException("Write a subject first.");
                    await mail.SendTestAsync(a.Perms, subject, body, me?.Email ?? "", title, baseUrl);
                    return await Again($"Test sent to {me!.Email}. Check how it looks, then send it to the list.", null);
                }
                var c = await mail.SendCampaignAsync(a.Perms, listId, subject, body, a.Actor, title, baseUrl);
                return FlashCookie.Redirect(http, "/admin/mail/sent", $"Sending “{c.Subject}” to {c.Recipients} subscribers.");
            }
            catch (Exception ex) when (ex is BlockException or NotFoundException) { return await Again(null, ex.Message); }
        });

        admin.MapGet("/mail/sent", async (HttpContext http, HttpRequest req, MailService mail) =>
        {
            var a = AdminContext.Get(http);
            var campaigns = await mail.CampaignsAsync(a.Perms);
            var url = ListUrl.From(req, "status", "size", "page");
            OutboxStatus? status = Enum.TryParse<OutboxStatus>(url["status"], out var st) ? st : null;
            var (message, _) = FlashCookie.Take(http);
            return Html.Render<MailSentPage>(new
            {
                Campaigns = campaigns, Progress = await mail.CampaignProgressAsync(campaigns.Select(c => c.Id)),
                ListNames = (await mail.ListsAsync()).ToDictionary(l => l.Id, l => l.Name),
                Outbox = await mail.OutboxAsync(a.Perms, status, Int(url["page"]), Int(url["size"])), Url = url, Message = message,
            });
        });

        // ---------------------------------------------------------------- templates and system emails
        admin.MapGet("/mail/templates", async (HttpContext http, MailService mail) =>
        {
            MailService.RequireRead(AdminContext.Get(http).Perms);
            var (message, error) = FlashCookie.Take(http);
            return Html.Render<MailTemplatesPage>(new { Templates = await mail.TemplatesAsync(), CanWrite = CanWrite(http), Message = message, Error = error });
        });
        admin.MapGet("/mail/system", async (HttpContext http, MailService mail) =>
        {
            MailService.RequireRead(AdminContext.Get(http).Perms);
            var (message, error) = FlashCookie.Take(http);
            return Html.Render<MailTemplatesPage>(new { System = true, Templates = await mail.TemplatesAsync(), CanWrite = CanWrite(http), Message = message, Error = error });
        });

        admin.MapGet("/mail/templates/new", (HttpContext http) =>
        {
            MailService.RequireWrite(AdminContext.Get(http).Perms);
            return Html.Render<MailTemplateEditPage>(new { Body = "Hello{{name.comma}}\n\n" });
        });
        admin.MapGet("/mail/templates/{id:int}", async (int id, HttpContext http, MailService mail) =>
        {
            MailService.RequireWrite(AdminContext.Get(http).Perms);
            var t = (await mail.TemplatesAsync()).FirstOrDefault(x => x.Id == id && x.Key is null) ?? throw new NotFoundException();
            var (message, error) = FlashCookie.Take(http);
            return Html.Render<MailTemplateEditPage>(new { Id = (int?)t.Id, t.Name, t.Subject, t.Body, Message = message, Error = error });
        });
        admin.MapPost("/mail/templates", async (HttpContext http, MailService mail) => await SaveTemplate(http, mail, null));
        admin.MapPost("/mail/templates/{id:int}", async (int id, HttpContext http, MailService mail) => await SaveTemplate(http, mail, id));
        admin.MapPost("/mail/templates/{id:int}/delete", async (int id, HttpContext http, MailService mail) =>
        {
            var a = AdminContext.Get(http);
            await mail.DeleteTemplateAsync(a.Perms, id, a.Actor);
            return FlashCookie.Redirect(http, "/admin/mail/templates", "Template deleted.");
        });

        admin.MapGet("/mail/system/{key}", async (string key, HttpContext http, MailService mail) =>
        {
            MailService.RequireWrite(AdminContext.Get(http).Perms);
            if (SystemEmails.Get(key) is null) throw new NotFoundException();
            var (subject, body, enabled) = await mail.SystemTextAsync(key);
            var edited = (await mail.TemplatesAsync()).Any(t => t.Key == key);
            var (message, error) = FlashCookie.Take(http);
            return Html.Render<MailTemplateEditPage>(new { Key = key, Subject = subject, Body = body, Enabled = enabled, Edited = edited, Message = message, Error = error });
        });
        admin.MapPost("/mail/system/{key}", async (string key, HttpContext http, MailService mail) =>
        {
            var a = AdminContext.Get(http);
            var f = await http.Request.ReadFormAsync();
            try { await mail.SaveTemplateAsync(a.Perms, null, key, "", f["subject"].ToString(), f["body"].ToString(), f["enabled"] == "true", a.Actor); }
            catch (BlockException ex)
            {
                return Html.Render<MailTemplateEditPage>(new { Key = key, Subject = f["subject"].ToString(), Body = f["body"].ToString(), Enabled = f["enabled"] == "true", Edited = true, Error = ex.Message });
            }
            return FlashCookie.Redirect(http, $"/admin/mail/system/{key}", "Saved.");
        });
        admin.MapPost("/mail/system/{key}/reset", async (string key, HttpContext http, MailService mail) =>
        {
            var a = AdminContext.Get(http);
            await mail.ResetSystemAsync(a.Perms, key, a.Actor);
            return FlashCookie.Redirect(http, $"/admin/mail/system/{key}", "Back to the built-in text.");
        });
    }

    private static async Task<IResult> SaveTemplate(HttpContext http, MailService mail, int? id)
    {
        var a = AdminContext.Get(http);
        var f = await http.Request.ReadFormAsync();
        string name = f["name"].ToString(), subject = f["subject"].ToString(), body = f["body"].ToString();
        try
        {
            var t = await mail.SaveTemplateAsync(a.Perms, id, null, name, subject, body, true, a.Actor);
            return FlashCookie.Redirect(http, $"/admin/mail/templates/{t.Id}", id is null ? "Template created." : "Saved.");
        }
        catch (BlockException ex) { return Html.Render<MailTemplateEditPage>(new { Id = id, Name = name, Subject = subject, Body = body, Error = ex.Message }); }
    }
}

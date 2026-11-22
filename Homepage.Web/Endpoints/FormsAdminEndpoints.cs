using System.Globalization;
using System.Text;
using System.Text.Json;
using Homepage.Blocks;
using Homepage.Content;
using Homepage.Forms;
using Homepage.Mail;
using Homepage.Permissions;
using Homepage.Web.Admin;
using Homepage.Web.Rendering;
using Microsoft.AspNetCore.OutputCaching;

namespace Homepage.Web.Endpoints;

/// <summary>/admin/forms: building forms (forms: rw) and reading what visitors sent (forms: ro).</summary>
public static class FormsAdminEndpoints
{
    private static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>
    /// The editor's fields and settings. The consent checkbox comes as its label: keys are made on the server
    /// (<see cref="FormService.CleanFields"/>), so the browser cannot know a new field's key.
    /// </summary>
    private static (FormSettings Settings, List<FormField> Fields, string ConsentLabel) Read(IFormCollection f)
    {
        List<FormField> fields;
        try { fields = JsonSerializer.Deserialize<List<FormField>>(f["fields"].ToString() is { Length: > 0 } j ? j : "[]", FormField.Json) ?? []; }
        catch (JsonException) { throw new BlockException("The fields could not be read."); }
        try { fields = FormService.CleanFields(fields); } catch (BlockException) { /* SaveAsync reports it; keep what was typed */ }
        var consentLabel = f["consentField"].ToString().Trim();
        var consentKey = consentLabel.Length == 0 ? null
            : fields.FirstOrDefault(x => x.Type == FieldType.Checkbox && x.Label.Trim() == consentLabel)?.Key ?? "?";
        var settings = new FormSettings(f["name"].ToString(), f["enabled"] == "true", f["submitLabel"].ToString(), f["successMessage"].ToString(),
            f["successUrl"].ToString().Trim(), f["notifyEmails"].ToString(), f["confirmToSender"] == "true", Int(f["mailingListId"]), consentKey);
        return (settings, fields, consentLabel);
    }

    private static string ConsentLabel(Form form) =>
        FormField.Parse(form.Fields).FirstOrDefault(x => x.Key == form.ConsentFieldKey)?.Label ?? "";

    public static void MapFormsAdmin(this RouteGroupBuilder admin)
    {
        admin.MapGet("/forms", async (HttpContext http, FormService forms, MailService mail) =>
        {
            var a = AdminContext.Get(http);
            FormService.RequireRead(a.Perms);
            var (message, error) = FlashCookie.Take(http);
            return Html.Render<FormsPage>(new
            {
                Forms = await forms.ListAsync(), Counts = await forms.CountsAsync(), CanWrite = a.Perms.HasSystem(SystemGroups.Forms, Perm.Rw),
                MailReady = mail.Options.Configured, Message = message, Error = error,
            });
        });

        admin.MapGet("/forms/new", async (HttpContext http, MailService mail) =>
        {
            FormService.RequireWrite(AdminContext.Get(http).Perms);
            FormField[] starter =
            [
                new() { Key = "name", Label = "Name", Type = FieldType.Text, Required = true, Width = FieldWidth.Half },
                new() { Key = "email", Label = "Email", Type = FieldType.Email, Required = true, Width = FieldWidth.Half },
                new() { Key = "message", Label = "Message", Type = FieldType.Textarea, Required = true },
            ];
            return Html.Render<FormEditPage>(new { Fields = starter, Lists = await mail.ListsAsync(), MailReady = mail.Options.Configured });
        });

        admin.MapGet("/forms/{id:int}", async (int id, HttpContext http, FormService forms, MailService mail) =>
        {
            FormService.RequireWrite(AdminContext.Get(http).Perms);
            var form = await forms.GetAsync(id) ?? throw new NotFoundException();
            var (message, error) = FlashCookie.Take(http);
            return Html.Render<FormEditPage>(new
            {
                Form = form, Settings = Settings(form), Fields = FormField.Parse(form.Fields), ConsentLabel = ConsentLabel(form), Lists = await mail.ListsAsync(),
                MailReady = mail.Options.Configured, Message = message, Error = error,
            });
        });

        admin.MapPost("/forms", async (HttpContext http, FormService forms, MailService mail) =>
        {
            var a = AdminContext.Get(http);
            var (settings, fields, consent) = Read(await http.Request.ReadFormAsync());
            try
            {
                var form = await forms.SaveAsync(a.Perms, null, settings, fields, a.Actor);
                return FlashCookie.Redirect(http, $"/admin/forms/{form.Id}", "Form created. Put it on a page with a Form block.");
            }
            catch (BlockException ex)
            {
                return Html.Render<FormEditPage>(new { Settings = settings, Fields = fields, ConsentLabel = consent, Lists = await mail.ListsAsync(), MailReady = mail.Options.Configured, Error = ex.Message });
            }
        });

        admin.MapPost("/forms/{id:int}", async (int id, HttpContext http, FormService forms, MailService mail, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            var (settings, fields, consent) = Read(await http.Request.ReadFormAsync());
            try { await forms.SaveAsync(a.Perms, id, settings, fields, a.Actor); }
            catch (BlockException ex)
            {
                var form = await forms.GetAsync(id) ?? throw new NotFoundException();
                return Html.Render<FormEditPage>(new { Form = form, Settings = settings, Fields = fields, ConsentLabel = consent, Lists = await mail.ListsAsync(), MailReady = mail.Options.Configured, Error = ex.Message });
            }
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default); // pages show the form's fields
            return FlashCookie.Redirect(http, $"/admin/forms/{id}", "Saved.");
        });

        admin.MapPost("/forms/{id:int}/delete", async (int id, HttpContext http, FormService forms, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            await forms.DeleteAsync(a.Perms, id, a.Actor);
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            return FlashCookie.Redirect(http, "/admin/forms", "Form deleted.");
        });

        admin.MapGet("/forms/{id:int}/submissions", async (int id, HttpContext http, HttpRequest req, FormService forms) =>
        {
            var a = AdminContext.Get(http);
            FormService.RequireRead(a.Perms);
            var form = await forms.GetAsync(id) ?? throw new NotFoundException();
            var url = ListUrl.From(req, "q", "show", "size", "page");
            var result = await forms.SubmissionsAsync(a.Perms, id, url["q"], url["show"] == "unread", Int(url["page"]), Int(url["size"]));
            var (message, error) = FlashCookie.Take(http);
            return Html.Render<FormSubmissionsPage>(new { Form = form, Result = result, Url = url, CanWrite = a.Perms.HasSystem(SystemGroups.Forms, Perm.Rw), Message = message, Error = error });
        });

        admin.MapGet("/forms/{id:int}/submissions/{sid:long}", async (int id, long sid, HttpContext http, FormService forms) =>
        {
            var a = AdminContext.Get(http);
            var form = await forms.GetAsync(id) ?? throw new NotFoundException();
            var s = await forms.SubmissionAsync(a.Perms, id, sid, markRead: true);
            return Html.Render<FormSubmissionPage>(new { Form = form, Submission = s, CanWrite = a.Perms.HasSystem(SystemGroups.Forms, Perm.Rw) });
        });

        admin.MapPost("/forms/{id:int}/submissions/bulk", async (int id, HttpContext http, FormService forms) =>
        {
            var a = AdminContext.Get(http);
            var f = await http.Request.ReadFormAsync();
            var ids = f["id"].Select(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0).Where(x => x > 0).Distinct().Take(500).ToList();
            var back = $"/admin/forms/{id}/submissions";
            if (ids.Count == 0) return FlashCookie.Redirect(http, back, "Select some answers first.", error: true);
            switch (f["action"].ToString())
            {
                case "read": await forms.SetReadAsync(a.Perms, id, ids, true); return FlashCookie.Redirect(http, back, "Marked as read.");
                case "unread": await forms.SetReadAsync(a.Perms, id, ids, false); return FlashCookie.Redirect(http, back, ids.Count == 1 ? "Marked as new." : $"{ids.Count} answers marked as new.");
                case "delete":
                    var n = await forms.DeleteSubmissionsAsync(a.Perms, id, ids, a.Actor);
                    return FlashCookie.Redirect(http, back, n == 1 ? "Answer deleted." : $"{n} answers deleted.");
                default: return Results.BadRequest();
            }
        });

        admin.MapGet("/forms/{id:int}/export", async (int id, HttpContext http, FormService forms) =>
        {
            var a = AdminContext.Get(http);
            var form = await forms.GetAsync(id) ?? throw new NotFoundException();
            var rows = await forms.AllSubmissionsAsync(a.Perms, id, a.Actor);
            var fields = FormField.Parse(form.Fields);
            var sb = new StringBuilder("received,page");
            foreach (var f in fields) sb.Append(',').Append(AdminEndpoints.Csv(f.Label));
            sb.Append('\n');
            foreach (var s in rows)
            {
                var data = FormService.ParseData(s.Data);
                sb.Append(s.AtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',').Append(AdminEndpoints.Csv(s.PageUrl));
                foreach (var f in fields) sb.Append(',').Append(AdminEndpoints.Csv(FormService.Text(data[f.Key])));
                sb.Append('\n');
            }
            var name = new string(form.Name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv; charset=utf-8",
                $"form-{(name.Length > 0 ? name : id.ToString(CultureInfo.InvariantCulture))}-{DateTime.UtcNow:yyyy-MM-dd}.csv");
        });
    }

    private static FormSettings Settings(Form f) =>
        new(f.Name, f.Enabled, f.SubmitLabel, f.SuccessMessage, f.SuccessUrl, f.NotifyEmails, f.ConfirmToSender, f.MailingListId, f.ConsentFieldKey);
}

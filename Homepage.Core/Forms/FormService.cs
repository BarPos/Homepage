using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Homepage.Blocks;
using Homepage.Common;
using Homepage.Content;
using Homepage.Data;
using Homepage.Permissions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Homepage.Forms;

/// <summary>The settings of a form besides its fields.</summary>
public sealed record FormSettings(string Name, bool Enabled, string SubmitLabel, string SuccessMessage, string SuccessUrl, string NotifyEmails,
    bool ConfirmToSender = false, int? MailingListId = null, string? ConsentFieldKey = null);

/// <summary>Forms: building them (forms: rw), checking and storing what visitors send, and reading submissions (forms: ro).</summary>
public sealed partial class FormService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache, IAuditLog audit, TimeProvider time)
{
    public const int MaxFields = 50, MaxOptions = 100;

    public static void RequireRead(EffectivePermissions p)
    {
        if (!p.HasSystem(SystemGroups.Forms, Perm.Ro)) throw new AccessDeniedException("Forms need forms: ro.");
    }

    public static void RequireWrite(EffectivePermissions p)
    {
        if (!p.HasSystem(SystemGroups.Forms, Perm.Rw)) throw new AccessDeniedException("Changing forms needs forms: rw.");
    }

    public async Task<List<Form>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Forms.AsNoTracking().OrderBy(f => f.Name).ToListAsync(ct);
    }

    /// <summary>A form for rendering on the site (cached briefly; forgotten when it is saved).</summary>
    public async Task<Form?> GetAsync(int id, CancellationToken ct = default)
    {
        var key = "form:" + id;
        if (cache.TryGetValue(key, out Form? f)) return f;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        f = await db.Forms.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        cache.Set(key, f, TimeSpan.FromMinutes(5));
        return f;
    }

    [GeneratedRegex("^[a-z0-9_]{1,40}$")]
    private static partial Regex KeyPattern();

    /// <summary>Cleans and checks the fields: labels, unique keys (made from labels), types, options.</summary>
    public static List<FormField> CleanFields(IEnumerable<FormField> fields)
    {
        var list = fields.ToList();
        if (list.Count > MaxFields) throw new BlockException($"A form can have at most {MaxFields} fields.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<FormField>();
        foreach (var f in list)
        {
            var label = (f.Label ?? "").Trim();
            if (label.Length == 0) continue; // an empty row
            if (label.Length > 200) throw new BlockException("Field labels can be at most 200 characters.");
            if (!Enum.IsDefined(f.Type)) throw new BlockException($"'{label}': unknown field type.");
            var key = (f.Key ?? "").Trim().ToLowerInvariant();
            if (!KeyPattern().IsMatch(key)) key = Slug.From(label).Replace('-', '_');
            if (key.Length == 0) key = "field";
            if (key.Length > 40) key = key[..40];
            var unique = key;
            for (var i = 2; !keys.Add(unique); i++) unique = $"{key}_{i}";
            var options = f.HasOptions
                ? (f.Options ?? []).Select(o => (o ?? "").Trim()).Where(o => o.Length > 0).Select(o => o.Length > 200 ? o[..200] : o).Distinct().ToList()
                : [];
            if (f.HasOptions && options.Count == 0) throw new BlockException($"'{label}': add at least one choice.");
            if (options.Count > MaxOptions) throw new BlockException($"'{label}': at most {MaxOptions} choices.");
            result.Add(new FormField
            {
                Key = unique, Label = label, Type = f.Type, Required = f.Required, Options = options,
                Placeholder = Clip(f.Placeholder, 200), Help = Clip(f.Help, 500), Width = Enum.IsDefined(f.Width) ? f.Width : FieldWidth.Full,
            });
        }
        return result;
    }

    private static string Clip(string? s, int max) => (s ?? "").Trim() is var t && t.Length > max ? t[..max] : (s ?? "").Trim();

    public async Task<Form> SaveAsync(EffectivePermissions p, int? id, FormSettings settings, IEnumerable<FormField> fields, string actor,
        CancellationToken ct = default)
    {
        RequireWrite(p);
        var name = settings.Name.Trim();
        if (name.Length is 0 or > 100) throw new BlockException("The form name must be 1 to 100 characters.");
        var clean = CleanFields(fields);
        if (clean.Count == 0) throw new BlockException("Add at least one field.");
        var url = (settings.SuccessUrl ?? "").Trim();
        if (url.Length > 0 && !SafeUrlAttribute.IsSafe(url)) throw new BlockException($"'{url}' is not an allowed link.");
        var emails = Mail.MailAddresses.CleanList(settings.NotifyEmails, out var bad);
        if (bad is not null) throw new BlockException($"'{bad}' is not an email address.");
        if (settings.ConfirmToSender && !clean.Any(f => f.Type == FieldType.Email))
            throw new BlockException("A confirmation to the sender needs an Email field.");
        var consent = string.IsNullOrWhiteSpace(settings.ConsentFieldKey) ? null : settings.ConsentFieldKey;
        if (settings.MailingListId is not null)
        {
            if (!clean.Any(f => f.Type == FieldType.Email)) throw new BlockException("Adding senders to a mailing list needs an Email field.");
            if (consent is null || !clean.Any(f => f.Key == consent && f.Type == FieldType.Checkbox))
                throw new BlockException("Adding senders to a mailing list needs a checkbox they tick to agree (choose it below).");
        }
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (settings.MailingListId is { } lid && !await db.MailingLists.AnyAsync(l => l.Id == lid, ct)) throw new NotFoundException("The mailing list does not exist.");
        Form form;
        var now = time.GetUtcNow().UtcDateTime;
        if (id is { } fid) form = await db.Forms.FirstOrDefaultAsync(f => f.Id == fid, ct) ?? throw new NotFoundException();
        else { form = new Form { CreatedAtUtc = now }; db.Forms.Add(form); }
        form.Name = name;
        form.Enabled = settings.Enabled;
        form.SubmitLabel = Clip(settings.SubmitLabel, 60) is { Length: > 0 } l ? l : "Send";
        form.SuccessMessage = Clip(settings.SuccessMessage, 2000) is { Length: > 0 } m ? m : "Thank you! Your message has been sent.";
        form.SuccessUrl = url;
        form.NotifyEmails = string.Join(", ", emails);
        form.ConfirmToSender = settings.ConfirmToSender;
        form.MailingListId = settings.MailingListId;
        form.ConsentFieldKey = settings.MailingListId is null ? null : consent;
        form.Fields = FormField.ToJson(clean);
        form.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        cache.Remove("form:" + form.Id);
        await audit.WriteAsync(actor, id is null ? "form.create" : "form.update", "Form", form.Id.ToString(CultureInfo.InvariantCulture),
            $"{name} ({clean.Count} fields)", ct: ct);
        return form;
    }

    public async Task DeleteAsync(EffectivePermissions p, int id, string actor, CancellationToken ct = default)
    {
        RequireWrite(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var form = await db.Forms.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException();
        var count = await db.FormSubmissions.CountAsync(s => s.FormId == id, ct);
        db.Forms.Remove(form); // submissions go with it (cascade)
        await db.SaveChangesAsync(ct);
        cache.Remove("form:" + id);
        await audit.WriteAsync(actor, "form.delete", "Form", id.ToString(CultureInfo.InvariantCulture), $"{form.Name} and {count} submissions", ct: ct);
    }

    // ---------------------------------------------------------------- what visitors send

    public sealed record Checked(Dictionary<string, JsonNode?> Data, Dictionary<string, string> Errors)
    {
        public bool Ok => Errors.Count == 0;
    }

    [GeneratedRegex(@"^[0-9+()\-.\s/]{3,30}$")]
    private static partial Regex PhonePattern();

    /// <summary>Checks the answers against the fields. <paramref name="values"/> gives every value sent for a name.</summary>
    public static Checked Check(Form form, Func<string, IReadOnlyList<string>> values)
    {
        var data = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in FormField.Parse(form.Fields))
        {
            var all = values(f.Key).Select(v => (v ?? "").Trim().Replace("\r\n", "\n")).ToList();
            var v = all.FirstOrDefault() ?? "";
            string? error = null;
            switch (f.Type)
            {
                case FieldType.Checkbox:
                    var on = v is "on" or "true" or "yes" or "1";
                    if (f.Required && !on) error = "Please tick this box.";
                    data[f.Key] = on;
                    break;
                case FieldType.Checkboxes:
                    var picked = all.Where(x => f.Options.Contains(x)).Distinct().ToList();
                    if (all.Any(x => x.Length > 0 && !f.Options.Contains(x))) error = "Choose from the list.";
                    else if (f.Required && picked.Count == 0) error = "Choose at least one.";
                    data[f.Key] = new JsonArray(picked.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                    break;
                default:
                    if (v.Length == 0) { if (f.Required) error = "Please fill this in."; data[f.Key] = ""; break; }
                    if (v.Length > f.MaxLength) error = $"At most {f.MaxLength} characters.";
                    else error = f.Type switch
                    {
                        FieldType.Email when !Mail.MailAddresses.IsValid(v) => "Enter an email address.",
                        FieldType.Number when !decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _) => "Enter a number.",
                        FieldType.Phone when !PhonePattern().IsMatch(v) => "Enter a phone number.",
                        FieldType.Url when !(Uri.TryCreate(v, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") => "Enter a web address (https://…).",
                        FieldType.Date when !DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => "Enter a date.",
                        FieldType.Select or FieldType.Radio when !f.Options.Contains(v) => "Choose from the list.",
                        FieldType.Text or FieldType.Email or FieldType.Phone or FieldType.Number or FieldType.Url or FieldType.Date when v.Contains('\n') => "One line only.",
                        _ => null,
                    };
                    data[f.Key] = v;
                    break;
            }
            if (error is not null) errors[f.Key] = error;
        }
        return new Checked(data, errors);
    }

    public async Task<FormSubmission> StoreAsync(Form form, Dictionary<string, JsonNode?> data, string pageUrl, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = new FormSubmission
        {
            FormId = form.Id, AtUtc = time.GetUtcNow().UtcDateTime, PageUrl = Clip(pageUrl, 300),
            Data = new JsonObject(data.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone()))).ToJsonString(),
        };
        db.FormSubmissions.Add(s);
        await db.SaveChangesAsync(ct);
        return s;
    }

    // ---------------------------------------------------------------- reading submissions (forms: ro)

    public async Task<PagedResult<FormSubmission>> SubmissionsAsync(EffectivePermissions p, int formId, string? search, bool unreadOnly, int? page, int? size,
        CancellationToken ct = default)
    {
        RequireRead(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.FormSubmissions.AsNoTracking().Where(s => s.FormId == formId);
        if (unreadOnly) q = q.Where(s => !s.Read);
        if (!string.IsNullOrWhiteSpace(search)) { var like = Paging.Like(search); q = q.Where(s => EF.Functions.ILike(s.Data, like)); }
        return await q.OrderByDescending(s => s.AtUtc).ThenByDescending(s => s.Id).ToPageAsync(page, size, ct);
    }

    public async Task<FormSubmission> SubmissionAsync(EffectivePermissions p, int formId, long id, bool markRead, CancellationToken ct = default)
    {
        RequireRead(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.FormSubmissions.FirstOrDefaultAsync(x => x.Id == id && x.FormId == formId, ct) ?? throw new NotFoundException();
        if (markRead && !s.Read) { s.Read = true; await db.SaveChangesAsync(ct); }
        return s;
    }

    public async Task<Dictionary<int, (int Total, int Unread)>> CountsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.FormSubmissions.GroupBy(s => s.FormId).Select(g => new { g.Key, Total = g.Count(), Unread = g.Count(s => !s.Read) }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => (x.Total, x.Unread));
    }

    public async Task<int> UnreadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.FormSubmissions.CountAsync(s => !s.Read, ct);
    }

    public async Task SetReadAsync(EffectivePermissions p, int formId, IReadOnlyCollection<long> ids, bool read, CancellationToken ct = default)
    {
        RequireRead(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.FormSubmissions.Where(s => s.FormId == formId && ids.Contains(s.Id)).ExecuteUpdateAsync(u => u.SetProperty(s => s.Read, read), ct);
    }

    public async Task<int> DeleteSubmissionsAsync(EffectivePermissions p, int formId, IReadOnlyCollection<long> ids, string actor, CancellationToken ct = default)
    {
        RequireWrite(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var n = await db.FormSubmissions.Where(s => s.FormId == formId && ids.Contains(s.Id)).ExecuteDeleteAsync(ct);
        await audit.WriteAsync(actor, "form.submissions.delete", "Form", formId.ToString(CultureInfo.InvariantCulture), $"{n} submissions", ct: ct);
        return n;
    }

    public async Task<List<FormSubmission>> AllSubmissionsAsync(EffectivePermissions p, int formId, string actor, CancellationToken ct = default)
    {
        RequireWrite(p); // exporting copies the data out: rw
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.FormSubmissions.AsNoTracking().Where(s => s.FormId == formId).OrderBy(s => s.AtUtc).ToListAsync(ct);
        await audit.WriteAsync(actor, "form.submissions.export", "Form", formId.ToString(CultureInfo.InvariantCulture), $"{list.Count} submissions", ct: ct);
        return list;
    }

    /// <summary>An answer as text, for lists, exports and emails.</summary>
    public static string Text(JsonNode? value) => value switch
    {
        null => "",
        JsonArray a => string.Join(", ", a.Select(x => x?.GetValue<string>() ?? "")),
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "Yes" : "No",
        JsonValue v => v.ToString(),
        _ => value.ToJsonString(),
    };

    public static JsonObject ParseData(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? []; } catch (JsonException) { return []; }
    }
}

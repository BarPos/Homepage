using System.Globalization;
using System.Security.Cryptography;
using Homepage.Blocks;
using Homepage.Common;
using Homepage.Content;
using Homepage.Data;
using Homepage.Permissions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Homepage.Mail;

/// <summary>
/// Email: system emails from templates, mailing lists (double opt-in, unsubscribe), emails to a list, and the outbox
/// that a background worker sends from (so a slow or unreachable mail server never slows a page, and nothing is lost).
/// </summary>
public sealed class MailService(IDbContextFactory<AppDbContext> dbFactory, EmailRenderer renderer, IOptionsMonitor<MailOptions> options,
    IAuditLog audit, TimeProvider time)
{
    public MailOptions Options => options.CurrentValue;

    public static void RequireRead(EffectivePermissions p)
    {
        if (!p.HasSystem(SystemGroups.Mail, Perm.Ro)) throw new AccessDeniedException("Mail needs mail: ro.");
    }

    public static void RequireWrite(EffectivePermissions p)
    {
        if (!p.HasSystem(SystemGroups.Mail, Perm.Rw)) throw new AccessDeniedException("Changing mail settings or sending needs mail: rw.");
    }

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    // ---------------------------------------------------------------- templates and system emails

    /// <summary>The text of a system email: the edited template when there is one, else the built-in default.</summary>
    public async Task<(string Subject, string Body, bool Enabled)> SystemTextAsync(string key, CancellationToken ct = default)
    {
        var def = SystemEmails.Get(key) ?? throw new ArgumentException("Unknown system email " + key);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await db.EmailTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        return t is null ? (def.Subject, def.Body, true) : (t.Subject, t.Body, t.Enabled);
    }

    public async Task<List<EmailTemplate>> TemplatesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.EmailTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<EmailTemplate> SaveTemplateAsync(EffectivePermissions p, int? id, string? key, string name, string subject, string body, bool enabled,
        string actor, CancellationToken ct = default)
    {
        RequireWrite(p);
        if (key is not null && SystemEmails.Get(key) is null) throw new NotFoundException("Unknown system email.");
        name = name.Trim();
        subject = subject.Trim();
        if (key is not null) name = SystemEmails.Get(key)!.Name;
        if (name.Length is 0 or > 100) throw new BlockException("The name must be 1 to 100 characters.");
        if (subject.Length is 0 or > 200) throw new BlockException("The subject must be 1 to 200 characters.");
        if (body.Length > 50_000) throw new BlockException("The text is too long.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        EmailTemplate t;
        if (key is not null) t = await db.EmailTemplates.FirstOrDefaultAsync(x => x.Key == key, ct) ?? db.EmailTemplates.Add(new EmailTemplate { Key = key }).Entity;
        else if (id is { } tid) t = await db.EmailTemplates.FirstOrDefaultAsync(x => x.Id == tid && x.Key == null, ct) ?? throw new NotFoundException();
        else t = db.EmailTemplates.Add(new EmailTemplate()).Entity;
        t.Name = name;
        t.Subject = subject;
        t.Body = body.Replace("\r\n", "\n");
        t.Enabled = key is null || enabled;
        t.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, key is null ? "mail.template" : "mail.system", "EmailTemplate", key ?? t.Id.ToString(CultureInfo.InvariantCulture), name, ct: ct);
        return t;
    }

    /// <summary>A system email back to its built-in text.</summary>
    public async Task ResetSystemAsync(EffectivePermissions p, string key, string actor, CancellationToken ct = default)
    {
        RequireWrite(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.EmailTemplates.Where(t => t.Key == key).ExecuteDeleteAsync(ct);
        await audit.WriteAsync(actor, "mail.system.reset", "EmailTemplate", key, "", ct: ct);
    }

    public async Task DeleteTemplateAsync(EffectivePermissions p, int id, string actor, CancellationToken ct = default)
    {
        RequireWrite(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await db.EmailTemplates.FirstOrDefaultAsync(x => x.Id == id && x.Key == null, ct) ?? throw new NotFoundException();
        db.EmailTemplates.Remove(t);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, "mail.template.delete", "EmailTemplate", id.ToString(CultureInfo.InvariantCulture), t.Name, ct: ct);
    }

    /// <summary>Queues a system email (unless it is turned off). Returns false when it is off.</summary>
    public async Task<bool> SendSystemAsync(string key, string to, string toName, Dictionary<string, string> values, string siteTitle, string siteUrl,
        string replyTo = "", string? unsubscribeUrl = null, CancellationToken ct = default)
    {
        var (subject, body, enabled) = await SystemTextAsync(key, ct);
        if (!enabled || !MailAddresses.IsValid(to)) return false;
        values["site.title"] = siteTitle;
        values["site.url"] = siteUrl;
        var r = renderer.Render(subject, body, values, siteTitle, unsubscribeUrl);
        await EnqueueAsync([new OutboxMessage { To = to, ToName = toName, Subject = r.Subject, Html = r.Html, Text = r.Text, ReplyTo = replyTo, Kind = key, UnsubscribeUrl = unsubscribeUrl ?? "" }], ct);
        return true;
    }

    public async Task EnqueueAsync(IReadOnlyCollection<OutboxMessage> messages, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var m in messages)
        {
            m.CreatedAtUtc = now;
            m.NextAttemptAtUtc = now;
            m.Status = OutboxStatus.Pending;
            db.Outbox.Add(m);
        }
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- mailing lists

    public async Task<List<MailingList>> ListsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MailingLists.AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);
    }

    public async Task<Dictionary<int, (int Subscribed, int Pending)>> ListCountsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.Subscribers.GroupBy(s => s.ListId).Select(g => new
        {
            g.Key, Subscribed = g.Count(s => s.Status == SubscriberStatus.Subscribed), Pending = g.Count(s => s.Status == SubscriberStatus.Pending),
        }).ToListAsync(ct)).ToDictionary(x => x.Key, x => (x.Subscribed, x.Pending));
    }

    public async Task<MailingList> SaveListAsync(EffectivePermissions p, int? id, string name, string description, bool doubleOptIn, string actor, CancellationToken ct = default)
    {
        RequireWrite(p);
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new BlockException("The list name must be 1 to 100 characters.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = id is { } lid ? await db.MailingLists.FirstOrDefaultAsync(l => l.Id == lid, ct) ?? throw new NotFoundException()
                                 : db.MailingLists.Add(new MailingList { CreatedAtUtc = time.GetUtcNow().UtcDateTime }).Entity;
        list.Name = name;
        list.Description = description.Trim().Length > 500 ? description.Trim()[..500] : description.Trim();
        list.DoubleOptIn = doubleOptIn;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, id is null ? "mail.list.create" : "mail.list.update", "MailingList", list.Id.ToString(CultureInfo.InvariantCulture), name, ct: ct);
        return list;
    }

    public async Task DeleteListAsync(EffectivePermissions p, int id, string actor, CancellationToken ct = default)
    {
        RequireWrite(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.MailingLists.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException();
        if (await db.Forms.AnyAsync(f => f.MailingListId == id, ct)) throw new ConflictException("A form adds people to this list. Change that form first.");
        var n = await db.Subscribers.CountAsync(s => s.ListId == id, ct);
        db.MailingLists.Remove(list); // subscribers and campaigns go with it
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, "mail.list.delete", "MailingList", id.ToString(CultureInfo.InvariantCulture), $"{list.Name} and {n} subscribers", ct: ct);
    }

    public async Task<PagedResult<Subscriber>> SubscribersAsync(EffectivePermissions p, int listId, string? search, SubscriberStatus? status, int? page, int? size,
        CancellationToken ct = default)
    {
        RequireRead(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Subscribers.AsNoTracking().Where(s => s.ListId == listId);
        if (status is { } st) q = q.Where(s => s.Status == st);
        if (!string.IsNullOrWhiteSpace(search)) { var like = Paging.Like(search); q = q.Where(s => EF.Functions.ILike(s.Email, like) || EF.Functions.ILike(s.Name, like)); }
        return await q.OrderByDescending(s => s.CreatedAtUtc).ThenByDescending(s => s.Id).ToPageAsync(page, size, ct);
    }

    /// <param name="DoubleOptIn">The list asks new subscribers to confirm: what a public sign-up tells everyone, known or not.</param>
    public sealed record SubscribeResult(Subscriber Subscriber, bool NeedsConfirmation, bool AlreadySubscribed, bool DoubleOptIn);

    /// <summary>
    /// Subscribes (or re-subscribes) an address. With double opt-in the subscriber stays pending and gets the confirm
    /// email; otherwise they are subscribed and get the welcome email. <paramref name="confirmed"/>: an admin adding someone
    /// who already agreed elsewhere.
    /// </summary>
    public async Task<SubscribeResult> SubscribeAsync(int listId, string email, string name, string source, string siteTitle, string baseUrl,
        bool confirmed = false, CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant();
        if (!MailAddresses.IsValid(email)) throw new BlockException("Enter an email address.");
        name = name.Trim().Length > 100 ? name.Trim()[..100] : name.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.MailingLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listId, ct) ?? throw new NotFoundException("The mailing list does not exist.");
        var s = await db.Subscribers.FirstOrDefaultAsync(x => x.ListId == listId && x.Email == email, ct);
        var now = time.GetUtcNow().UtcDateTime;
        if (s is { Status: SubscriberStatus.Subscribed }) return new SubscribeResult(s, false, true, list.DoubleOptIn);
        if (s is null)
        {
            s = new Subscriber { ListId = listId, Email = email, CreatedAtUtc = now, Token = NewToken(), Source = source };
            db.Subscribers.Add(s);
        }
        if (name.Length > 0) s.Name = name;
        var needsConfirm = list.DoubleOptIn && !confirmed;
        s.Status = needsConfirm ? SubscriberStatus.Pending : SubscriberStatus.Subscribed;
        if (!needsConfirm) { s.ConfirmedAtUtc = now; s.UnsubscribedAtUtc = null; }
        await db.SaveChangesAsync(ct);
        var values = new Dictionary<string, string>
        {
            ["list.name"] = list.Name, ["name"] = s.Name, ["name.comma"] = s.Name.Length > 0 ? $" {s.Name}," : ",", ["email"] = s.Email,
            ["confirm.url"] = $"{baseUrl}/hp/mail/confirm/{s.Token}", ["unsubscribe.url"] = $"{baseUrl}/hp/mail/unsubscribe/{s.Token}",
        };
        if (needsConfirm) await SendSystemAsync(SystemEmails.ListConfirm, s.Email, s.Name, values, siteTitle, baseUrl, ct: ct);
        else if (!confirmed) await SendSystemAsync(SystemEmails.ListWelcome, s.Email, s.Name, values, siteTitle, baseUrl, unsubscribeUrl: values["unsubscribe.url"], ct: ct);
        return new SubscribeResult(s, needsConfirm, false, list.DoubleOptIn);
    }

    /// <summary>The link in the confirm email. Returns the list, or null for an unknown link.</summary>
    public async Task<MailingList?> ConfirmAsync(string token, string siteTitle, string baseUrl, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Subscribers.FirstOrDefaultAsync(x => x.Token == token, ct);
        if (s is null) return null;
        var list = await db.MailingLists.AsNoTracking().FirstAsync(l => l.Id == s.ListId, ct);
        if (s.Status == SubscriberStatus.Pending)
        {
            s.Status = SubscriberStatus.Subscribed;
            s.ConfirmedAtUtc = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            var unsub = $"{baseUrl}/hp/mail/unsubscribe/{s.Token}";
            await SendSystemAsync(SystemEmails.ListWelcome, s.Email, s.Name, new Dictionary<string, string>
            {
                ["list.name"] = list.Name, ["name"] = s.Name, ["name.comma"] = s.Name.Length > 0 ? $" {s.Name}," : ",", ["email"] = s.Email, ["unsubscribe.url"] = unsub,
            }, siteTitle, baseUrl, unsubscribeUrl: unsub, ct: ct);
        }
        return list;
    }

    /// <summary>Unsubscribes the owner of a link. Returns the list, or null for an unknown link.</summary>
    public async Task<MailingList?> UnsubscribeAsync(string token, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Subscribers.FirstOrDefaultAsync(x => x.Token == token, ct);
        if (s is null) return null;
        if (s.Status != SubscriberStatus.Unsubscribed)
        {
            s.Status = SubscriberStatus.Unsubscribed;
            s.UnsubscribedAtUtc = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }
        return await db.MailingLists.AsNoTracking().FirstAsync(l => l.Id == s.ListId, ct);
    }

    public async Task<MailingList?> ListOfTokenAsync(string token, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listId = await db.Subscribers.Where(x => x.Token == token).Select(x => (int?)x.ListId).FirstOrDefaultAsync(ct);
        return listId is null ? null : await db.MailingLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listId, ct);
    }

    public async Task SetSubscriberStatusAsync(EffectivePermissions p, int listId, long id, SubscriberStatus status, string actor, CancellationToken ct = default)
    {
        RequireWrite(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Subscribers.FirstOrDefaultAsync(x => x.Id == id && x.ListId == listId, ct) ?? throw new NotFoundException();
        s.Status = status;
        if (status == SubscriberStatus.Unsubscribed) s.UnsubscribedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, "mail.subscriber." + status.ToString().ToLowerInvariant(), "MailingList", listId.ToString(CultureInfo.InvariantCulture), s.Email, ct: ct);
    }

    public async Task DeleteSubscriberAsync(EffectivePermissions p, int listId, long id, string actor, CancellationToken ct = default)
    {
        RequireWrite(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Subscribers.FirstOrDefaultAsync(x => x.Id == id && x.ListId == listId, ct) ?? throw new NotFoundException();
        db.Subscribers.Remove(s);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, "mail.subscriber.delete", "MailingList", listId.ToString(CultureInfo.InvariantCulture), s.Email, ct: ct);
    }

    public async Task<List<Subscriber>> AllSubscribersAsync(EffectivePermissions p, int listId, string actor, CancellationToken ct = default)
    {
        RequireWrite(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var all = await db.Subscribers.AsNoTracking().Where(s => s.ListId == listId).OrderBy(s => s.Email).ToListAsync(ct);
        await audit.WriteAsync(actor, "mail.list.export", "MailingList", listId.ToString(CultureInfo.InvariantCulture), $"{all.Count} subscribers", ct: ct);
        return all;
    }

    // ---------------------------------------------------------------- emails to a list

    public async Task<List<Campaign>> CampaignsAsync(EffectivePermissions p, CancellationToken ct = default)
    {
        RequireRead(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Campaigns.AsNoTracking().OrderByDescending(c => c.CreatedAtUtc).Take(200).ToListAsync(ct);
    }

    public async Task<Dictionary<int, (int Sent, int Pending, int Failed)>> CampaignProgressAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var list = ids.ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.Outbox.Where(m => m.CampaignId != null && list.Contains(m.CampaignId.Value)).GroupBy(m => m.CampaignId!.Value)
                .Select(g => new { g.Key, Sent = g.Count(m => m.Status == OutboxStatus.Sent), Pending = g.Count(m => m.Status == OutboxStatus.Pending), Failed = g.Count(m => m.Status == OutboxStatus.Failed) })
                .ToListAsync(ct)).ToDictionary(x => x.Key, x => (x.Sent, x.Pending, x.Failed));
    }

    /// <summary>A preview or test: one rendered email to one address (not recorded as a campaign).</summary>
    public async Task SendTestAsync(EffectivePermissions p, string subject, string body, string to, string siteTitle, string baseUrl, CancellationToken ct = default)
    {
        RequireWrite(p);
        if (!MailAddresses.IsValid(to)) throw new BlockException("Your account has no valid email address (set it on your profile).");
        var r = renderer.Render("[Test] " + subject, body, new Dictionary<string, string>
        {
            ["name"] = "Test", ["name.comma"] = " Test,", ["email"] = to, ["site.title"] = siteTitle, ["site.url"] = baseUrl, ["unsubscribe.url"] = baseUrl + "/hp/mail/unsubscribe/test",
        }, siteTitle, baseUrl + "/hp/mail/unsubscribe/test");
        await EnqueueAsync([new OutboxMessage { To = to, Subject = r.Subject, Html = r.Html, Text = r.Text, Kind = "test" }], ct);
    }

    /// <summary>Sends an email to everyone subscribed to a list (queued; the worker sends at the configured rate).</summary>
    public async Task<Campaign> SendCampaignAsync(EffectivePermissions p, int listId, string subject, string body, string actor, string siteTitle, string baseUrl,
        CancellationToken ct = default)
    {
        RequireWrite(p);
        subject = subject.Trim();
        if (subject.Length is 0 or > 200) throw new BlockException("The subject must be 1 to 200 characters.");
        if (body.Trim().Length == 0) throw new BlockException("Write the email first.");
        if (body.Length > 100_000) throw new BlockException("The email is too long.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.MailingLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listId, ct) ?? throw new NotFoundException("The mailing list does not exist.");
        var subscribers = await db.Subscribers.AsNoTracking().Where(s => s.ListId == listId && s.Status == SubscriberStatus.Subscribed).ToListAsync(ct);
        if (subscribers.Count == 0) throw new BlockException("Nobody is subscribed to this list yet.");
        var now = time.GetUtcNow().UtcDateTime;
        var campaign = new Campaign
        {
            ListId = listId, Subject = subject, Body = body.Replace("\r\n", "\n"), Status = CampaignStatus.Sent, Recipients = subscribers.Count,
            CreatedBy = actor, CreatedAtUtc = now, SentAtUtc = now,
        };
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct);
        var messages = new List<OutboxMessage>(subscribers.Count);
        foreach (var s in subscribers)
        {
            var unsub = $"{baseUrl}/hp/mail/unsubscribe/{s.Token}";
            var r = renderer.Render(campaign.Subject, campaign.Body, new Dictionary<string, string>
            {
                ["name"] = s.Name, ["name.comma"] = s.Name.Length > 0 ? $" {s.Name}," : ",", ["email"] = s.Email, ["list.name"] = list.Name,
                ["site.title"] = siteTitle, ["site.url"] = baseUrl, ["unsubscribe.url"] = unsub,
            }, siteTitle, unsub);
            messages.Add(new OutboxMessage
            {
                To = s.Email, ToName = s.Name, Subject = r.Subject, Html = r.Html, Text = r.Text, Kind = "campaign", CampaignId = campaign.Id, UnsubscribeUrl = unsub,
            });
        }
        await EnqueueAsync(messages, ct);
        await audit.WriteAsync(actor, "mail.send", "MailingList", listId.ToString(CultureInfo.InvariantCulture), $"\"{subject}\" to {subscribers.Count} subscribers of {list.Name}", ct: ct);
        return campaign;
    }

    public async Task<PagedResult<OutboxMessage>> OutboxAsync(EffectivePermissions p, OutboxStatus? status, int? page, int? size, CancellationToken ct = default)
    {
        RequireRead(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Outbox.AsNoTracking().AsQueryable();
        if (status is { } st) q = q.Where(m => m.Status == st);
        return await q.OrderByDescending(m => m.CreatedAtUtc).ThenByDescending(m => m.Id).ToPageAsync(page, size, ct);
    }

    public async Task<int> RetryFailedAsync(EffectivePermissions p, string actor, CancellationToken ct = default)
    {
        RequireWrite(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var n = await db.Outbox.Where(m => m.Status == OutboxStatus.Failed).ExecuteUpdateAsync(u => u
            .SetProperty(m => m.Status, OutboxStatus.Pending).SetProperty(m => m.Attempts, 0).SetProperty(m => m.NextAttemptAtUtc, time.GetUtcNow().UtcDateTime), ct);
        await audit.WriteAsync(actor, "mail.retry", "Outbox", "", $"{n} emails", ct: ct);
        return n;
    }
}

/// <summary>Sends the outbox: at most MaxPerMinute emails a minute, retrying failures with growing pauses (up to 6 tries).</summary>
internal sealed class OutboxWorker(IDbContextFactory<AppDbContext> dbFactory, IOptionsMonitor<MailOptions> options, TimeProvider time, ILogger<OutboxWorker> logger)
    : BackgroundService
{
    private const int MaxAttempts = 6;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var sent = 0;
            try { sent = await RunOnceAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Sending mail failed"); }
            try { await Task.Delay(sent > 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10), stop); } catch (OperationCanceledException) { return; }
            await CleanupAsync(stop);
        }
    }

    private DateTime _minuteStart;
    private int _sentThisMinute;
    private DateTime _lastCleanup;

    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.Configured) return 0;
        var now = time.GetUtcNow().UtcDateTime;
        if (now - _minuteStart >= TimeSpan.FromMinutes(1)) { _minuteStart = now; _sentThisMinute = 0; }
        var room = Math.Max(1, o.MaxPerMinute) - _sentThisMinute;
        if (room <= 0) return 0;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var due = await db.Outbox.Where(m => m.Status == OutboxStatus.Pending && m.NextAttemptAtUtc <= now).OrderBy(m => m.Id).Take(Math.Min(room, 50)).ToListAsync(ct);
        if (due.Count == 0) return 0;
        using var smtp = new SmtpClient { Timeout = 30_000 };
        try
        {
            await smtp.ConnectAsync(o.Host, o.Port, Security(o), ct);
            if (!string.IsNullOrEmpty(o.Username)) await smtp.AuthenticateAsync(o.Username, o.Password, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var m in due) Fail(m, "Cannot reach the mail server: " + ex.Message, now);
            await db.SaveChangesAsync(ct);
            return 0;
        }
        var sent = 0;
        foreach (var m in due)
        {
            try
            {
                await smtp.SendAsync(Build(m, o), ct);
                m.Status = OutboxStatus.Sent;
                m.SentAtUtc = time.GetUtcNow().UtcDateTime;
                m.Error = "";
                m.Html = m.Kind == "campaign" ? "" : m.Html; // the campaign keeps the text once; no copy per recipient
                sent++;
                _sentThisMinute++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Fail(m, ex.Message, now); }
        }
        await db.SaveChangesAsync(ct);
        try { await smtp.DisconnectAsync(true, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "SMTP disconnect"); }
        return sent;
    }

    private static void Fail(OutboxMessage m, string error, DateTime now)
    {
        m.Attempts++;
        m.Error = error.Length > 500 ? error[..500] : error;
        if (m.Attempts >= MaxAttempts) m.Status = OutboxStatus.Failed;
        else m.NextAttemptAtUtc = now.AddMinutes(Math.Pow(3, m.Attempts)); // 3, 9, 27, 81, 243 minutes
    }

    private static SecureSocketOptions Security(MailOptions o) => o.Security.ToLowerInvariant() switch
    {
        "none" => SecureSocketOptions.None,
        "starttls" => SecureSocketOptions.StartTls,
        "sslonconnect" or "ssl" or "tls" => SecureSocketOptions.SslOnConnect,
        _ => o.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable,
    };

    internal static MimeMessage Build(OutboxMessage m, MailOptions o)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        msg.To.Add(new MailboxAddress(m.ToName, m.To));
        if (MailAddresses.IsValid(m.ReplyTo)) msg.ReplyTo.Add(MailboxAddress.Parse(m.ReplyTo));
        msg.Subject = m.Subject;
        if (m.UnsubscribeUrl.Length > 0)
        {
            msg.Headers.Add("List-Unsubscribe", $"<{m.UnsubscribeUrl}>");
            msg.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
        }
        msg.Body = new BodyBuilder { HtmlBody = m.Html, TextBody = m.Text }.ToMessageBody();
        return msg;
    }

    /// <summary>Sent emails are kept 90 days as the history; failed ones too.</summary>
    private async Task CleanupAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (now - _lastCleanup < TimeSpan.FromHours(6)) return;
        _lastCleanup = now;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var before = now.AddDays(-90);
            await db.Outbox.Where(m => m.Status != OutboxStatus.Pending && m.CreatedAtUtc < before).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "Outbox cleanup"); }
    }
}

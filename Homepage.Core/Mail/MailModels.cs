namespace Homepage.Mail;

/// <summary>
/// SMTP settings (appsettings / the config file / HOMEPAGE_Mail__… variables). Mail is sent only when Host and
/// FromAddress are set; until then emails wait in the outbox.
/// </summary>
public sealed class MailOptions
{
    public const string Section = "Mail";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    /// <summary>Auto (by port), None, StartTls, SslOnConnect.</summary>
    public string Security { get; set; } = "Auto";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    /// <summary>At most this many emails per minute (many providers limit bursts).</summary>
    public int MaxPerMinute { get; set; } = 60;

    public bool Configured => !string.IsNullOrWhiteSpace(Host) && MailAddresses.IsValid(FromAddress);
}

public enum OutboxStatus { Pending = 0, Sent = 1, Failed = 2 }

/// <summary>An email waiting to be sent, or sent. Kept for a while as the sending history.</summary>
public sealed class OutboxMessage
{
    public long Id { get; set; }
    public string To { get; set; } = "";
    public string ToName { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Html { get; set; } = "";
    public string Text { get; set; } = "";
    public string ReplyTo { get; set; } = "";
    /// <summary>One-click unsubscribe address (List-Unsubscribe header), for mailing-list mail.</summary>
    public string UnsubscribeUrl { get; set; } = "";
    /// <summary>What sent it: a system email key (form.notification, …) or "campaign".</summary>
    public string Kind { get; set; } = "";
    public int? CampaignId { get; set; }
    public OutboxStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public string Error { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
}

/// <summary>An email template: a subject and a Markdown body with {{placeholders}}. System emails have a Key.</summary>
public sealed class EmailTemplate
{
    public int Id { get; set; }
    /// <summary>The system email it customizes (form.notification, …); null for templates of your own.</summary>
    public string? Key { get; set; }
    public string Name { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>System emails only: off = not sent.</summary>
    public bool Enabled { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class MailingList
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>New subscribers confirm by email before they get anything (recommended, and required by law in many places).</summary>
    public bool DoubleOptIn { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

public enum SubscriberStatus { Pending = 0, Subscribed = 1, Unsubscribed = 2 }

public sealed class Subscriber
{
    public long Id { get; set; }
    public int ListId { get; set; }
    /// <summary>Lower case.</summary>
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public SubscriberStatus Status { get; set; }
    /// <summary>Secret for confirm and unsubscribe links.</summary>
    public string Token { get; set; } = "";
    /// <summary>Where they came from: subscribe block, a form, added by an admin, import.</summary>
    public string Source { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
    public DateTime? UnsubscribedAtUtc { get; set; }
}

public enum CampaignStatus { Draft = 0, Sending = 1, Sent = 2 }

/// <summary>An email an admin writes and sends to a mailing list.</summary>
public sealed class Campaign
{
    public int Id { get; set; }
    public int ListId { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public CampaignStatus Status { get; set; }
    public int Recipients { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
}

public static class MailAddresses
{
    public static bool IsValid(string? email)
    {
        email = (email ?? "").Trim();
        return email.Length is > 3 and <= 254 && System.Net.Mail.MailAddress.TryCreate(email, out var a) && a.Address == email && email.Contains('.', StringComparison.Ordinal)
               && !email.Any(char.IsWhiteSpace);
    }

    /// <summary>A comma/semicolon/space separated list → distinct addresses; <paramref name="bad"/> is the first invalid one.</summary>
    public static List<string> CleanList(string? list, out string? bad)
    {
        bad = null;
        var result = new List<string>();
        foreach (var part in (list ?? "").Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsValid(part)) { bad ??= part; continue; }
            if (!result.Contains(part, StringComparer.OrdinalIgnoreCase)) result.Add(part);
        }
        return result;
    }
}

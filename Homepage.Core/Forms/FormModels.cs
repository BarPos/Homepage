using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Homepage.Forms;

/// <summary>A form the site's visitors fill in (contact, sign-up, …). Its fields are JSON (<see cref="FormField"/>).</summary>
public sealed class Form
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>The fields, in order (jsonb).</summary>
    public string Fields { get; set; } = "[]";
    public string SubmitLabel { get; set; } = "Send";
    /// <summary>Shown after a successful submission (plain text, line breaks kept).</summary>
    public string SuccessMessage { get; set; } = "Thank you! Your message has been sent.";
    /// <summary>Instead of the message: go to this page (an address or page:12, …). Empty = show the message.</summary>
    public string SuccessUrl { get; set; } = "";
    /// <summary>Email addresses that get each submission (comma separated). Needs the email settings.</summary>
    public string NotifyEmails { get; set; } = "";
    /// <summary>Off: the form shows that it is closed and takes no submissions.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Send the person who filled it in a confirmation (to the first Email field), using the system email template.</summary>
    public bool ConfirmToSender { get; set; }
    /// <summary>Add the sender to this mailing list when they tick <see cref="ConsentFieldKey"/> (they still confirm by email).</summary>
    public int? MailingListId { get; set; }
    public string? ConsentFieldKey { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class FormSubmission
{
    public long Id { get; set; }
    public int FormId { get; set; }
    public DateTime AtUtc { get; set; }
    /// <summary>The answers (jsonb): field key → text, or a list of texts for checkboxes.</summary>
    public string Data { get; set; } = "{}";
    /// <summary>The page the form was sent from (path only).</summary>
    public string PageUrl { get; set; } = "";
    public bool Read { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldType
{
    [Display(Name = "Short text")] Text,
    [Display(Name = "Email")] Email,
    [Display(Name = "Long text")] Textarea,
    [Display(Name = "Number")] Number,
    [Display(Name = "Phone")] Phone,
    [Display(Name = "Web address")] Url,
    [Display(Name = "Date")] Date,
    [Display(Name = "Dropdown")] Select,
    [Display(Name = "Choice (radio buttons)")] Radio,
    [Display(Name = "Checkboxes (several)")] Checkboxes,
    [Display(Name = "Single checkbox (yes/no, consent)")] Checkbox,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldWidth { Full, Half }

/// <summary>One field of a form.</summary>
public sealed class FormField
{
    /// <summary>Stable name of the answer in submissions and exports (made from the label, unique in the form).</summary>
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public FieldType Type { get; set; } = FieldType.Text;
    public bool Required { get; set; }
    public string Placeholder { get; set; } = "";
    public string Help { get; set; } = "";
    /// <summary>Choices for dropdowns, radio buttons and checkboxes.</summary>
    public List<string> Options { get; set; } = [];
    public FieldWidth Width { get; set; } = FieldWidth.Full;

    public bool HasOptions => Type is FieldType.Select or FieldType.Radio or FieldType.Checkboxes;
    public int MaxLength => Type switch { FieldType.Textarea => 5000, FieldType.Url => 500, _ => 200 };

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<FormField> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<FormField>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string ToJson(IEnumerable<FormField> fields) => JsonSerializer.Serialize(fields, Json);
}

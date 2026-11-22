namespace Homepage.Mail;

/// <summary>An email the site sends by itself. Its text can be changed under Mail → System emails.</summary>
public sealed record SystemEmail(string Key, string Name, string Description, string Subject, string Body, IReadOnlyList<(string Name, string Meaning)> Variables);

public static class SystemEmails
{
    private static readonly (string, string) SiteTitle = ("site.title", "the site's title");
    private static readonly (string, string) SiteUrl = ("site.url", "the site's address");

    public const string FormNotification = "form.notification";
    public const string FormConfirmation = "form.confirmation";
    public const string ListConfirm = "list.confirm";
    public const string ListWelcome = "list.welcome";
    public const string AccountCreated = "account.created";
    public const string PasswordChanged = "account.password";
    public const string TwoFactorChanged = "account.2fa";

    public static readonly IReadOnlyList<SystemEmail> All =
    [
        new(FormNotification, "Form: new submission",
            "To the addresses set on a form, for every submission. Reply goes to the sender's email when the form has an Email field.",
            "New message from {{form.name}}",
            "**{{form.name}}** got a new submission on [{{site.title}}]({{site.url}}).\n\n{{answers}}\n\n[See all submissions]({{admin.url}})",
            [("form.name", "the form's name"), ("answers", "every question and answer"), ("admin.url", "the submissions in the admin"), ("page.url", "the page it was sent from"), SiteTitle, SiteUrl]),
        new(FormConfirmation, "Form: confirmation to the sender",
            "To the person who filled in a form (its first Email field), when the form has \"Send the sender a confirmation\" on.",
            "We got your message",
            "Hello,\n\nthank you for writing to **{{site.title}}**. We got your message and will get back to you.\n\n{{answers}}",
            [("form.name", "the form's name"), ("answers", "what they sent"), SiteTitle, SiteUrl]),
        new(ListConfirm, "Mailing list: confirm the subscription",
            "To a new subscriber of a list with double opt-in: they are subscribed only after they click the link.",
            "Please confirm your subscription to {{list.name}}",
            "Hello{{name.comma}}\n\nplease confirm that you want to get **{{list.name}}** from {{site.title}}:\n\n[Yes, subscribe me]({{confirm.url}})\n\nIf you did not ask for this, ignore this email: nothing will be sent.",
            [("list.name", "the list's name"), ("confirm.url", "the confirmation link"), ("name", "their name, if they gave one"), ("name.comma", "\", Name,\" or \",\""), SiteTitle, SiteUrl]),
        new(ListWelcome, "Mailing list: welcome",
            "To a subscriber once they are subscribed (after confirming, or at once on lists without double opt-in).",
            "Welcome to {{list.name}}",
            "Hello{{name.comma}}\n\nyou are now subscribed to **{{list.name}}** from {{site.title}}.\n\nYou can [unsubscribe]({{unsubscribe.url}}) at any time.",
            [("list.name", "the list's name"), ("unsubscribe.url", "the unsubscribe link"), ("name", "their name"), ("name.comma", "\", Name,\" or \",\""), SiteTitle, SiteUrl]),
        new(AccountCreated, "Account: created",
            "To a new user's email address when an administrator creates their account.",
            "Your account on {{site.title}}",
            "Hello {{user.name}},\n\nan account was created for you on **{{site.title}}**. Your user name is **{{user.login}}**; your administrator gives you the password.\n\n[Sign in]({{signin.url}})",
            [("user.name", "their display name"), ("user.login", "their user name"), ("signin.url", "the sign-in page"), SiteTitle, SiteUrl]),
        new(PasswordChanged, "Account: password changed",
            "To a user when their password changes, so they notice if it was not them.",
            "Your password on {{site.title}} was changed",
            "Hello {{user.name}},\n\nthe password of your account **{{user.login}}** on {{site.title}} was just changed.\n\nIf this was not you, contact your administrator at once.",
            [("user.name", "their display name"), ("user.login", "their user name"), SiteTitle, SiteUrl]),
        new(TwoFactorChanged, "Account: two-factor sign-in turned on or off",
            "To a user when two-factor sign-in is turned on or off for their account.",
            "Two-factor sign-in is {{state}} on {{site.title}}",
            "Hello {{user.name}},\n\ntwo-factor sign-in is now **{{state}}** for your account **{{user.login}}** on {{site.title}}.\n\nIf this was not you, contact your administrator at once.",
            [("user.name", "their display name"), ("user.login", "their user name"), ("state", "on or off"), SiteTitle, SiteUrl]),
    ];

    public static SystemEmail? Get(string key) => All.FirstOrDefault(e => e.Key == key);
}

---
title: Mail
parent: Admin
nav_order: 8
---

# Mail
{: .no_toc }

1. TOC
{:toc}

**Mail** in the admin is everything the site sends by email: answers to [forms](forms.md), newsletters to mailing
lists, and notices about accounts. Emails go through an SMTP server you set in the
[configuration](../server/configuration.md#mail).

Looking needs `mail: ro`; changing lists, templates and system emails, and sending, need `mail: rw` (see
[Permissions](../permissions.md)). Administrators have both.

## Overview and outbox

**Overview** shows the SMTP server the site uses (or how to set one up), **Send a test email to me** (to the
address on your profile), and the outbox.

Every email is queued first and sent in the background, at most `Mail:MaxPerMinute` a minute. An email that cannot
be sent is tried again after 3, 9, 27, … minutes, up to 6 times, then marked **failed** (the error is shown);
**Try the failed ones again** queues them once more. Sent and failed emails are kept 90 days. While no SMTP server
is set, emails wait in the outbox and go out once one is.

## Mailing lists

A list has a name (subscribers see it), a description (only shown in the admin) and a choice:

- **Confirm by email (double opt-in, recommended)**: new subscribers get an email with a link and join only after
  they open it and press *Yes, subscribe me*. The extra button is there because mail systems open links to check
  them; a link alone does not subscribe anyone.
- Otherwise they are subscribed at once and get the welcome email.

People join with a [Newsletter sign-up block](blocks.md#newsletter-sign-up), a form's consent checkbox, or when you
add them on the list's page (tick *They already agreed* to skip the confirmation email). The sign-up block answers
the same whether an address is new or already on the list, so nobody can find out who is subscribed.

Every email to a list ends with an unsubscribe link, and carries the `List-Unsubscribe` and
`List-Unsubscribe-Post` headers, so mail apps show their own one-click *Unsubscribe* button. **Unsubscribe** on the
list's page stops emails but keeps the address; **Delete** forgets it. **Export CSV** downloads the list.

## Sending to a list

**Send email**: choose the list, optionally start from a [template](#templates), write the subject and the text in
Markdown. Placeholders: `{{name}}`, `{{name.comma}}` (" Ann," with a name, "," without), `{{email}}`,
`{{list.name}}`, `{{site.title}}`, `{{site.url}}`. **Send a test to me** sends it to your own address first.
**Send to the list** queues one personal email for each subscribed address; **Sent** shows the progress and the
history.

## Templates

Ready-made emails (name, subject, text) to start from in **Send email**.

## System emails

The emails the site sends by itself. Each can be edited (the placeholders it knows are listed under the text), turned
off, or reset to the built-in text.

| Email | Sent |
|---|---|
| Form: new answer | To the form's notification addresses |
| Form: copy to the sender | To the sender, when the form asks for it |
| Mailing list: confirm | To a new subscriber of a double opt-in list |
| Mailing list: welcome | When someone is subscribed |
| Account: created | To a new user with an email address |
| Account: password changed | When a user's password changes (by them or an administrator) |
| Account: two-factor sign-in turned on or off | When it changes for a user |

Values from visitors (names, answers) are inserted as plain text, never as Markdown or HTML.

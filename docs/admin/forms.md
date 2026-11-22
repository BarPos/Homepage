---
title: Forms
parent: Admin
nav_order: 7
---

# Forms
{: .no_toc }

1. TOC
{:toc}

**Forms** in the admin builds forms for the public site (a contact form, a sign-up, a short survey) and keeps what
visitors send. A form shows on a page with the [Form block](blocks.md#form).

Reading answers needs `forms: ro`; building forms, deleting answers and exporting them need `forms: rw` (see
[Permissions](../permissions.md)). Administrators have both.

## Building a form

**New form** starts with Name, Email and Message. For each field:

| | |
|---|---|
| **Label** | What visitors see. The field's key (used in the export and in emails) is made from it, once. |
| **Type** | *Short text*, *Email*, *Long text*, *Number*, *Phone*, *Web address*, *Date*, *Dropdown*, *Choice (radio buttons)*, *Checkboxes (several)*, *Single checkbox (yes/no, consent)* |
| **Choices** | For dropdowns, radio buttons and checkboxes: one per line |
| **Placeholder**, **Help text** | A hint in the empty field; a line under it |
| **Required** | The form is not sent without it |
| **Half width** | Two half-width fields sit side by side (one under the other on phones) |

The form's own settings:

| | |
|---|---|
| **Open** | Off: the page says the form is closed, and the server refuses it |
| **Button text** | `Send` by default |
| **Thank-you message**, **Or go to a page** | After sending, the form is replaced by the message, or the visitor goes to the chosen page |
| **Tell these addresses about new answers** | One or more addresses (commas between). Each gets the answers by email; *Reply* goes to the sender when the form has an Email field. |
| **Send the sender a copy** | The sender gets their answers (needs an Email field) |
| **Add to a mailing list**, **Consent checkbox** | Senders who tick the chosen single checkbox join the [mailing list](mail.md#mailing-lists) (with its confirmation email when the list uses double opt-in) |

The emails' text is in [Mail → System emails](mail.md#system-emails). Without a configured SMTP server, answers are
still kept; the emails wait in the outbox.

## Answers

The forms list shows how many answers each form has and how many are new; **Forms** in the top bar shows the number
of new answers. Opening a form shows its answers, newest first, searchable. Opening an answer marks it read; select
several to mark them read or new, or to delete them. **Reply by email** opens your mail program.

**Export CSV** downloads every answer (UTF-8, one column per field). Values that start with `=`, `+`, `-` or `@`
are quoted so spreadsheets do not run them as formulas. Exports and deletions are written to the audit log.

## Spam

Forms have no captcha. Instead:

- a hidden field that people do not see but bots fill in: such a submission looks successful to the bot and is
  dropped;
- a signed timestamp: answers sent within 2 seconds of loading the form, or with a form older than a week, are
  refused;
- at most 10 submissions a minute per network;
- size limits per field (200 characters, 5,000 for long text) and 64 KB per submission.

---
title: Admin
nav_order: 5
has_children: true
---

# Admin
{: .no_toc }

1. TOC
{:toc}

The admin is at `https://<site>/admin`. Everything in it is filtered by your [permissions](../permissions.md): a page
you may not see is not listed, a button you may not use is not shown, and the server refuses the action anyway.

## Signing in

- Accounts are kept in the database (ASP.NET Core Identity, PBKDF2 password hashes). Passwords have at least 10
  characters.
- Wrong passwords are limited per account and network (5 per 15 minutes from one network), not by locking the
  account, so nobody can keep you out by guessing. Every failure shows the same message; the **Audit log** has the
  details.
- An account that is **invalid**, or that has no permission on any group, cannot sign in.
- A session ends after 8 hours without activity, 12 hours after sign-in, or when the password is reset or the account
  made invalid. Permissions are checked on every request, so taking a role away takes effect on the user's next click.
- With [two-factor sign-in](#your-profile) turned on, the password is followed by a 6-digit code from an
  authenticator app, or one of the account's recovery codes. The code step has the same limits as the password.
- Lost the only admin password, or the phone with the authenticator? On the server:
  `sudo bash install.sh --reset-password`. It also turns two-factor sign-in off for that account.

## Your profile

Your name in the top bar opens **Your profile**:

| Section | |
|---|---|
| Account | **Display name** (shown in the admin and as the author of your posts) and **email**. The user name you sign in with is changed only by an administrator. |
| Password | Current password, new password twice. Your other sessions end; this one continues. |
| Two-factor sign-in | **Set up**: scan the QR code with an authenticator app (1Password, Bitwarden, Google Authenticator, …) or type the key, then enter the code it shows. You get **10 recovery codes**, shown once: keep them in your password manager. Each signs you in once without the phone. **New recovery codes** replaces them. **Turn off** asks for your password. |

An administrator can turn two-factor sign-in off for another account (**Users → Edit**), for example after a lost
phone.

## Pages

| Page | |
|---|---|
| **Dashboard** | Counts of pages, posts and drafts you can see, the active theme, **New page** / **New post**, recently changed items |
| **Pages**, **Posts** | Lists with search, filters (group, blog, status), sorting and paging. *Unpublished changes* marks a published item whose draft differs; *home* marks the home page. **Edit** opens the [visual editor](editor.md), **Settings** the item's settings. |
| Item settings | Title, address (slug), excerpt, blog (posts), group; publish, unpublish, delete; *Use as home page* (pages); published revisions |
| **Media** | **Upload files**, search, filter and delete files (see [Media and menus](media-menus.md)) |
| **Themes** | Activate, duplicate, edit design, header and footer as drafts, publish (see [Themes](themes.md)) |
| **Menus** | A list of menus; **New menu** and **Edit** open the menu editor (see [Media and menus](media-menus.md#menus), needs `layout: rw`) |
| **Blogs** | A list of blogs; **New blog** and **Edit** open the blog editor (see [Blogs](blogs.md), needs `layout: rw`) |
| **Users & permissions** | Users, Groups, Roles (each a list with **Add account** / **New group** / **New role** and an editor page), and the three relation screens (needs `admin: rw`, see [Permissions](../permissions.md)) |
| **Your profile** | Your display name, email, password and two-factor sign-in (every user) |
| **Settings** | Site title, tagline, logo, favicon, public address, home page, default group; version and updates (needs `admin: rw`) |
| **Audit log** | Who signed in, created, published, deleted, published themes, changed menus, settings, accounts and permissions, and from which address. Each entry has the request's IP, and *signed in from* when the user's session started at another address. Drafts (content autosave, theme drafts) are not logged; publishing is (needs `audit: ro`) |

## Lists

Every list in the admin (pages, posts, media, users, groups, roles, audit log) has the same controls:
- a **search** box that filters as you type;
- **filters** for the list (group, blog, status, kind, role, area, dates);
- **rows per page** (10, 25, 50, 100) and page links;
- **Clear filters**.

Filtering, counting and paging happen in the database, and only over items you may see. The filters are part of the
URL, so a view can be bookmarked or shared. The relation screens under **Users & permissions** are not paged yet.

## Creating content

**New page** or **New post** on the **Dashboard** or the **Pages** / **Posts** list opens a dialog: title, type
(*Page* or *Blog post*), blog (posts), group, **Create and edit**. The group list offers only groups where you have
`create`, and is left out when there is only one.

- A **page** starts with a section holding its title as a heading. Pages live at `/<slug>`.
- A **post** starts with one empty Markdown block and belongs to a **blog**, chosen when you create it (the form
  shows a blog list for posts). Posts live at `/<blog>/<slug>`, for example `/blog/hello-world` or
  `/releases/version-2`, and appear in that blog, its RSS feed and *Latest posts* blocks.
- A post shows its **author**: the display name of the account that created it, on the post, in post lists and as
  `dc:creator` in the feeds. The display name starts out as the user name, so set one on **Your profile** if your
  login name should not be public. Posts whose account was deleted, and the example post made at install, show no
  author.
- The address is made from the title (`About us` → `/about-us`) and can be changed in the item's settings.
  - **Pages and blogs share the first part of the address**: a page cannot take a blog's address and the other way
    round. A page titled like a blog gets a number (`/releases-2`).
  - A few addresses belong to the site itself: `admin`, `search`, `media`, `theme`, `healthz`, `content`,
    `feed-xml`, `sitemap-xml`, `robots-txt`.
  - A post's address only has to be unique within its blog.

## The home page

`/` shows either a **published page** or a **blog's list of posts**. Choose it in **Settings → Home page**, or with
**Use as home page** in a page's settings (admins only). The page lists mark the home page with *home*.

## Drafts, publishing and revisions

- The editor always changes the **draft**. Visitors see the **published** version until someone presses **Publish**
  (needs `publish` on the item's group).
- Publishing records a **revision** (title, blocks, time, who) and extracts the text for search and the excerpt.
- **Unpublish** takes the item off the site and keeps the draft. **Delete** removes it and its revisions.
- An author with only `create` can edit and delete their own items **until they are first published**; after that
  it takes `edit` / `delete`.

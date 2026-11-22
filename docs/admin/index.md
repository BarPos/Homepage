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
- After 5 wrong passwords an account is locked for 10 minutes. Sign-in attempts are also limited to 10 per minute per
  IP address. Every failure shows the same message; the reason is in the **Audit log**.
- An account that is **invalid**, or that has no permission on any group, cannot sign in.
- A session ends after 8 hours without activity. Permissions are checked on every request, so taking a role away
  takes effect on the user's next click.
- Lost the only admin password? On the server: `sudo bash install.sh --reset-password`.

{: .note }
Two-factor sign-in is not available yet.

## Pages

| Page | |
|---|---|
| **Dashboard** | Counts of pages, posts and drafts you can see, the active theme, a form to create a page or post, recently changed items |
| **Pages**, **Posts** | Lists with search and a group filter. *changes* marks a published item whose draft differs. **Edit** opens the [visual editor](editor.md), **Settings** the item's settings. |
| Item settings | Title, address (slug), excerpt, group; publish, unpublish, delete; published revisions |
| **Media** | Upload images and PDFs, see and delete files (see [Media and menus](media-menus.md)) |
| **Themes** | Activate, duplicate, edit design, header and footer (see [Themes](themes.md)) |
| **Menus** | The `main` and `footer` menus (needs `layout: rw`) |
| **Users & permissions** | Users, Groups, Roles, and the three relation screens (needs `admin: rw`, see [Permissions](../permissions.md)) |
| **Settings** | Site title, tagline, public address, home page, default group; version and updates (needs `admin: rw`) |
| **Audit log** | Who signed in, created, published, deleted, changed themes, menus, settings and permissions (needs `audit: ro`) |

## Creating content

On the **Dashboard** or the **Pages** / **Posts** list: title, type (*Page* or *Blog post*), group, **Create and
edit**. The group list offers only groups where you have `create`.

- A **page** starts with a section holding its title as a heading. Pages live at `/<slug>`.
- A **post** starts with one empty Markdown block. Posts live at `/blog/<slug>` and appear in the blog, the RSS feed
  and *Latest posts* blocks.
- The address is made from the title (`About us` → `/about-us`) and can be changed in the item's settings. A few
  addresses belong to the site itself and are never given to a page: `admin`, `blog`, `search`, `media`, `theme`,
  `healthz`, `feed-xml`, `sitemap-xml`, `robots-txt`.

## Drafts, publishing and revisions

- The editor always changes the **draft**. Visitors see the **published** version until someone presses **Publish**
  (needs `publish` on the item's group).
- Publishing records a **revision** (title, blocks, time, who) and extracts the text for search and the excerpt.
- **Unpublish** takes the item off the site and keeps the draft. **Delete** removes it and its revisions.
- An author with only `create` can edit and delete their own items **until they are first published**; after that
  it takes `edit` / `delete`.

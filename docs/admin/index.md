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
| **Pages**, **Posts** | Lists with search, filters (group, blog, status), sorting and paging. *changes* marks a published item whose draft differs; *home* marks the home page. **Edit** opens the [visual editor](editor.md), **Settings** the item's settings. |
| Item settings | Title, address (slug), excerpt, blog (posts), group; publish, unpublish, delete; *Use as home page* (pages); published revisions |
| **Media** | Upload images and PDFs, search, filter and delete files (see [Media and menus](media-menus.md)) |
| **Themes** | Activate, duplicate, edit design, header and footer (see [Themes](themes.md)) |
| **Menus** | Create, rename and delete menus and edit their links (needs `layout: rw`) |
| **Blogs** | Create blogs, change their addresses (see [Blogs](blogs.md), needs `layout: rw`) |
| **Users & permissions** | Users, Groups, Roles, and the three relation screens (needs `admin: rw`, see [Permissions](../permissions.md)) |
| **Settings** | Site title, tagline, logo, favicon, public address, home page, default group; version and updates (needs `admin: rw`) |
| **Audit log** | Who signed in, created, published, deleted, changed themes, menus, settings and permissions (needs `audit: ro`) |

## Lists

Every list in the admin (pages, posts, media, users, groups, roles, audit log) has the same controls:
- a **search** box that filters as you type;
- **filters** for the list (group, blog, status, kind, role, area, dates);
- **rows per page** (10, 25, 50, 100) and page links;
- **Clear filters**.

Filtering, counting and paging happen in the database, and only over items you may see. The filters are part of the
URL, so a view can be bookmarked or shared. The relation screens under **Users & permissions** are not paged yet.

## Creating content

On the **Dashboard** or the **Pages** / **Posts** list: title, type (*Page* or *Blog post*), group, **Create and
edit**. The group list offers only groups where you have `create`.

- A **page** starts with a section holding its title as a heading. Pages live at `/<slug>`.
- A **post** starts with one empty Markdown block and belongs to a **blog**, chosen when you create it (the form
  shows a blog list for posts). Posts live at `/<blog>/<slug>`, for example `/blog/hello-world` or
  `/releases/version-2`, and appear in that blog, its RSS feed and *Latest posts* blocks.
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

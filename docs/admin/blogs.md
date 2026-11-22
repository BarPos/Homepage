---
title: Blogs
parent: Admin
nav_order: 5
---

# Blogs
{: .no_toc }

1. TOC
{:toc}

A site can have any number of blogs. Each blog lists its posts at its own address and has its own RSS feed:

| Blog | Post list | A post | Feed |
|---|---|---|---|
| *Blog* (created at install) | `/blog` | `/blog/hello-world` | `/blog/feed.xml` |
| *Releases* (an example) | `/releases` | `/releases/version-2` | `/releases/feed.xml` |

`/feed.xml` has the latest posts of all blogs.

## Managing blogs

**Blogs** in the admin (needs `layout: rw`):

| Setting | |
|---|---|
| Name | Shown as the blog's title and in its feed |
| Address | The first part of the URL. Lower-case letters, digits and dashes. It cannot be a page's address or one of the site's own (`admin`, `search`, `media`, …). |
| Description | Shown under the title and in the feed |
| Posts per page | 1 to 50; *Load more* shows the next ones |

The **Blogs** page lists the blogs with their address and number of posts. **New blog** opens an empty blog editor,
**Edit** an existing one. The editor shows the addresses the blog, its posts and its feed will have as you type.

- **Delete blog** (in the editor): only when it has no posts and is not the last blog.
- **Posts** opens the post list filtered to that blog.

## Changing a blog's address

Change **Address** and **Save**. The old address keeps working: `/old` and `/old/<post>` answer with a permanent
redirect (`301`) to the new address, so links and search engines follow. The blog editor lists the old addresses that
redirect to it. If another blog later takes an old address, the redirect stops and the new blog answers there.

## Posts and blogs

- A new post goes into the blog chosen in the create form (the first blog by default).
- **Move** a post to another blog in its settings (**Blog**). Its address becomes `/<new blog>/<slug>`; a post with
  the same address in the target blog prevents the move.
- Posts lists can be filtered by blog. *Latest posts* blocks show one blog or all blogs.
- A blog can be the **home page**: **Settings → Home page → Blogs**.

Permissions on posts come from their **group**, not their blog: a blog is a place on the site, a group decides who may
edit (see [Permissions](../permissions.md)).

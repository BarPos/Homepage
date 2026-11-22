---
title: Architecture
nav_order: 3
---

# Architecture
{: .no_toc }

1. TOC
{:toc}

## Processes

```
Visitor ─────────HTTP(S)───► Public site ───┐
Editor ─HTTP(S), cookie on /admin─► Admin ───┼── one Server process ── EF Core ──► PostgreSQL (migrations)
                                             │        │
                                             │        └── data/: media, Data Protection keys, staged updates
                                             └── background jobs: release checks
```

- **One server process** (`Homepage.Server`, systemd service `homepage`) serves the public site, the admin and the
  visual editor, and runs the background jobs.
- **Server-rendered HTML.** Pages are Razor components rendered once per request (static server-side rendering, no
  render modes). The admin adds [htmx](https://htmx.org/) for partial updates, [Alpine.js](https://alpinejs.dev/)
  for small client-side state and [SortableJS](https://sortablejs.github.io/Sortable/) for drag and drop. All three
  ship with the server; nothing is loaded from a CDN.
- **No persistent connection.** Every editor action is one HTTP request; the server keeps no per-user state between
  requests. Public pages are cached in memory and served without touching the database.
- **Only the server contacts GitHub**, for updates, and only when updates are configured.

## Solution layout

| Project | Type | Role |
|---|---|---|
| `Homepage.Shared` | class library | `AppVersion` |
| `Homepage.Core` | class library | All logic, by area: `Data/`, `Permissions/`, `Blocks/`, `Themes/`, `Content/`, `Media/`, `Accounts/`, `Updates/`; EF Core model |
| `Homepage.Web` | Razor class library | Block renderers, public site, admin, visual editor, endpoints, static files |
| `Homepage.Server` | ASP.NET Core host | Program, configuration, command line (`--migrate-only`, `--create-local-account`, …), EF migrations |
| `Homepage.Tests` | xUnit v3 | Unit tests, and PostgreSQL tests through Testcontainers |
| `build/`, `deploy/linux/` | scripts | Release packaging; `install.sh`, `update.sh`, `backup.sh`, systemd units |

## Content: block trees

Every page and post is stored as one JSON document (a PostgreSQL `jsonb` column): a tree of typed blocks.

- **Each block type** has a C# props class and a Razor component that renders it. The same component renders the
  public page and the editor canvas, so the canvas is exactly what visitors will see.
- **Blocks never store HTML or CSS.** They store values such as `"padding": "Lg"` or `"background": "surface-alt"`.
  The theme turns those into colors and sizes, so changing the theme restyles every existing page.
- **Unknown block types** (from a newer or older release) are kept when a page is saved, shown as "unsupported" in
  the editor and left out of the public page.
- **Drafts and publishing:** the editor changes the draft tree. **Publish** copies it to the published tree, records
  a revision and extracts the text for search, excerpts and reading time.

The format is described in [Content format](reference/content-format.md).

## The visual editor

```
Editor page (admin)                         Canvas (iframe, same origin)
 ├─ palette, layers tree ── SortableJS        └─ the real page in edit mode: blocks wrapped
 ├─ inspector form ── htmx, 0.5 s debounce        with data-block-id, lists are drop zones
 └─ editor.js ── POST /admin/editor/{target}/ops   (SortableJS inside the canvas)
                 GET  …/tree, …/layers  ← re-rendered by the server after every change
```

1. Every change is an **operation**: `insert`, `move`, `delete`, `duplicate`, or a props update from the inspector.
2. The server checks the user's permission on the item, the block type and the nesting rules, applies the operation
   to the draft and saves it (optimistic concurrency on PostgreSQL's `xmin`).
3. The editor fetches the re-rendered tree and the layers list and swaps them in.

## Themes

A theme is a row with design tokens (`jsonb`), custom CSS, and a header and a footer block tree. The server turns the
tokens into CSS custom properties at `/theme/{id}/{version}.css`; every saved change increases the version, so the
URL changes and browsers can cache each version for a year. The site stylesheet (`site.css`) uses only those
variables. See [Themes](admin/themes.md).

## Data

PostgreSQL 16+ through EF Core (Npgsql). The schema is created and upgraded only by migrations
(`Homepage.Server --migrate-only`, also run at every start), followed by the content migrations of stored block trees.
The main tables:

- **Accounts:** `users` (ASP.NET Core Identity, without Identity roles), `user_claims`, `user_logins`, `user_tokens`.
- **Permissions:** `groups`, `roles`, `user_groups`, `role_groups`, `user_roles`.
- **Content:** `content_items` (pages and posts, draft and published trees, a generated `tsvector` for search),
  `revisions`, `media`, `menus`.
- **Design:** `themes`.
- **Administration:** `settings`, `audit_log`, `update_runs`.

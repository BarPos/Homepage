# Homepage

Self-hosted website software: a CMS / blogging platform with a drag-and-drop visual editor, themes and
OTRS-style permission groups. ASP.NET Core 10, PostgreSQL, static Razor components, htmx + Alpine.js +
SortableJS. There is no Blazor Server and no SignalR: every interaction is a plain HTTP request.

The design and its reasoning are in [techstack.md](techstack.md).

## Development

Requires the .NET 10 SDK and Docker (development and tests only; production uses no Docker).

```bash
docker compose up -d db
```

```bash
cd Homepage.Server && dotnet run -- --environment Development --migrate-only
```

```bash
cd Homepage.Server && printf '%s\n' 'choose-a-password' | dotnet run -- --environment Development --create-local-account admin
```

```bash
dotnet run --project Homepage.Server -- --environment Development
```

Open http://127.0.0.1:5080 for the site and http://127.0.0.1:5080/admin to sign in. The dev database listens on
127.0.0.1:5433 (user `homepage`, password `homepage-dev`); data such as media and keys goes to `.devdata/`.

Tests (the PostgreSQL ones start a throwaway `postgres:16` container through Testcontainers):

```bash
dotnet test --project Homepage.Tests
```

After changing the data model:

```bash
dotnet ef migrations add <Name> --project Homepage.Server --output-dir Data/Migrations
```

## Layout

| Project | Contents |
|---|---|
| `Homepage.Shared` | `AppVersion` (must match `<Version>` in `Directory.Build.props`) |
| `Homepage.Core` | Data model, permissions, blocks (tree, ops, validation, content migrations), themes, content, media, accounts, self-update |
| `Homepage.Web` | Block renderers, public site, admin, visual editor, endpoints, static files (`wwwroot`) |
| `Homepage.Server` | Host, command line, EF migrations |
| `Homepage.Tests` | Unit tests and PostgreSQL integration / end-to-end tests |
| `build/`, `deploy/linux/`, `.github/workflows/` | Release build, installer, updater, backup, systemd units, CI. Adapted from [DomainNotice](https://github.com/BarPos/DomainNotice) (MIT) |

## Status (first milestone)

**Built:** public site (pages, blog, search, RSS, sitemap, output cache), admin, visual editor (drag and drop in the
canvas and the layers tree, click-to-add, inspector, device preview, publish), Markdown blocks mixed with visual
blocks, themes (3 built in, duplicate, live theme editor, header/footer in the visual editor, menus), OTRS-style
permission groups (users, groups, roles, the three relation screens, effective-permission view), media uploads,
audit log, settings, self-update service, CLI, release build, installer/updater/backup scripts, CI workflows.

**Not built yet** (described in techstack.md): undo/redo, inline text editing, splitting and merging Markdown blocks,
rich-text (Tiptap) block, global blocks, page templates and section presets, theme import/export and theme
versions, self-hosted fonts, forms and form submissions, scheduled publishing, revision restore and trash, image
resizing, 2FA, member groups, external group mapping (OIDC/LDAP), release signing, Tailwind (hand-written CSS
on the same CSS variables is used instead).

## Production

Ubuntu 24.04+ with `install.sh` and systemd, self-update from the GitHub releases of
[BarPos/Homepage](https://github.com/BarPos/Homepage) with automatic rollback, and `backup.sh`. See techstack.md
section 8. Development builds make no update checks (`Updates:GitHubRepository` is empty in
`appsettings.Development.json`).

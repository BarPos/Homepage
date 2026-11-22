---
title: URLs
parent: Reference
nav_order: 2
---

# URLs

## Public site

| URL | | Cached |
|---|---|---|
| `/` | The home page chosen in **Settings**, or the latest posts | yes |
| `/<slug>` | A published page | yes |
| `/blog` | Published posts, 10 per page; `?skip=N` for more (htmx *Load more*) | yes |
| `/blog/<slug>` | A published post | yes |
| `/search?q=…` | Full-text search over published pages and posts (live search with htmx) | yes, per query |
| `/feed.xml` | RSS 2.0 of the 20 latest posts | yes |
| `/sitemap.xml` | Every published page and post | yes |
| `/robots.txt` | Disallows `/admin/`, points at the sitemap | yes |
| `/media/<file>` | An uploaded file, one-year immutable cache | browser |
| `/theme/<id>/<version>.css` | The CSS variables of a theme version, one-year immutable cache | browser |
| `/_content/Homepage.Web/…` | Static files: `site.css`, scripts, libraries (versioned with `?v=`) | browser |
| `/healthz` | `{"status":"ok","version":"…"}` for loopback; 404 for everyone else | no |

Pages are output-cached for up to 30 minutes and per value of the `HX-Request` / `HX-Boosted` headers, so an htmx
fragment is never served as a full page. Any publish, unpublish, delete or change to settings, themes or menus empties
the cache.

## Admin

All under `/admin`, signed in (except `login`), never cached. POST requests need the antiforgery token.

| URL | |
|---|---|
| `/admin/login`, `/admin/logout` | Sign in and out |
| `/admin` | Dashboard |
| `/admin/content?type=page\|post&groupId=&q=` | Lists |
| `/admin/content/new` (POST) | Create |
| `/admin/content/<id>` | Item settings; `/publish`, `/unpublish`, `/delete` (POST) |
| `/admin/editor/<target>` | Visual editor. `<target>` is `content-<id>`, `header-<themeId>` or `footer-<themeId>`. |
| `/admin/editor/<target>/canvas`, `/tree`, `/layers`, `/inspector/<blockId>` | Editor parts (HTML) |
| `/admin/editor/<target>/ops` (POST, JSON) | `{"op":"insert\|move\|delete\|duplicate","id":…,"type":…,"parentId":…,"index":…}` |
| `/admin/editor/<target>/props/<blockId>` (POST, form) | Inspector values, `p.<Property>` |
| `/admin/media` | Media; upload (POST, multipart), `/<id>/delete` |
| `/admin/themes`, `/admin/themes/<id>` | Themes, design editor; `/preview`, `/activate`, `/duplicate` |
| `/admin/menus`, `/admin/menus/<key>` | Menus |
| `/admin/users`, `/admin/users/<id>`, `/password` | Users |
| `/admin/groups`, `/admin/roles` | Groups and roles |
| `/admin/permissions/user-groups`, `/role-groups`, `/user-roles` | The relation screens |
| `/admin/settings`, `/admin/updates/check`, `/admin/updates/apply` | Settings and updates |
| `/admin/audit` | Audit log |

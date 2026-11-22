---
title: URLs
parent: Reference
nav_order: 2
---

# URLs

## Public site

| URL | | Cached |
|---|---|---|
| `/` | The home page chosen in **Settings**: a page, or a blog's posts | yes |
| `/<slug>` | A published page, or a blog's posts (the blog's page size; `?skip=N` for more, htmx *Load more*) | yes |
| `/<page>/<subpage>/…` | A subpage (up to 8 levels) | yes |
| `/<page>/<blog>`, `/<page>/<blog>/<slug>` | A blog under a page, and its posts and feed | yes |
| `/<blog>/<slug>` | A published post of that blog | yes |
| `/<blog>/feed.xml` | RSS 2.0 of the blog's 20 latest posts | yes |
| `/<old blog address>…` | `301` to the blog's current address (after a rename) | yes |
| `/search?q=…` | Full-text search over published pages and posts (live search with htmx) | yes, per query |
| `/feed.xml` | RSS 2.0 of the 20 latest posts of all blogs | yes |
| `/sitemap.xml` | Every blog, published page and post | yes |
| `/robots.txt` | Disallows `/admin/`, points at the sitemap | yes |
| `/favicon.ico` | Redirects to the ICO favicon (or the PNG, or the SVG) chosen in the settings; `404` without one | no |
| `/hp/event` (POST) | Page views and engagement time from the analytics script (JSON, at most 4 KB; only from this site's pages; `204`, or `429` above 120 per minute per network) | no |
| `/hp/forms/<id>` (POST) | A form's answers. With `Accept: application/json` answers `{"ok","message","errors":{field:message},"redirect"}`, otherwise a page or a redirect. 10 per minute per network. | no |
| `/hp/forms/token?scope=form:<id>` | A fresh anti-spam token for a form on a cached page (`no-store`) | no |
| `/hp/mail/subscribe/<listId>` (POST) | The Newsletter sign-up block (`email`, `name`); answers like `/hp/forms` | no |
| `/hp/mail/confirm/<token>` | The confirm page of a double opt-in subscription; POST confirms | no |
| `/hp/mail/unsubscribe/<token>` | The unsubscribe page; POST unsubscribes (also RFC 8058 one-click: `List-Unsubscribe=One-Click`) | no |
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
| `/admin/login`, `/admin/login/2fa`, `/admin/logout` | Sign in (password, then the two-factor code when turned on) and out |
| `/admin/profile` | Your profile; `/password`, `/2fa` (setup), `/2fa/recovery`, `/2fa/disable` (POST) |
| `/admin` | Dashboard |
| `/admin/content?type=page\|post&q=&group=&blog=&status=&sort=&size=&page=` | Lists (`status`: `draft`, `published`, `changed`; `sort`: `title`, `published`) |
| `/admin/content/new` (POST) | Create |
| `/admin/content/<id>` | Item settings (with `parentId` for pages); `/options` (theme, layout, CSS, no-index), `/publish`, `/unpublish`, `/delete`, `/home` (POST) |
| `/admin/editor/icons?q=`, `/admin/editor/links?q=` | The icon chooser and the link chooser (HTML fragments) |
| `/admin/editor/<target>` | Visual editor. `<target>` is `content-<id>`, `header-<themeId>` or `footer-<themeId>`. |
| `/admin/editor/<target>/canvas`, `/tree`, `/layers`, `/inspector/<blockId>` | Editor parts (HTML) |
| `/admin/editor/<target>/ops` (POST, JSON) | `{"op":"insert\|move\|delete\|duplicate","id":…,"type":…,"parentId":…,"index":…}` |
| `/admin/editor/<target>/props/<blockId>` (POST, form) | Inspector values, `p.<Property>` |
| `/admin/media?q=&group=&kind=&size=&page=` | Media; upload (POST, multipart), `/<id>/delete` |
| `/admin/themes`, `/admin/themes/<id>` | Themes, design editor (POST saves the draft); `/preview`, `/publish`, `/discard`, `/activate`, `/duplicate` |
| `/admin/menus`, `/admin/menus/new`, `/admin/menus/<key>`, `/<key>/delete` | Menus: list, empty editor, editor; create (POST `/admin/menus`), save, delete |
| `/admin/blogs`, `/admin/blogs/new`, `/admin/blogs/<id>`, `/<id>/delete` | Blogs: list, empty editor, editor; create or save (POST `/admin/blogs`), delete |
| `/admin/users?q=&status=&role=`, `/admin/users/new`, `/admin/users/<id>`, `/password`, `/2fa/disable` | Users (`q` also matches email) |
| `/admin/groups?q=&status=`, `/admin/groups/new`, `/admin/groups/<id>`; the same under `/admin/roles` | Groups and roles: lists and editors |
| `/admin/permissions/user-groups`, `/role-groups`, `/user-roles` | The relation screens |
| `/admin/settings`, `/admin/updates/check` | Settings, release check |
| `/admin/updates/apply` (POST) | Stage an update; with `Accept: application/json` answers `{"ok":true,"from":…,"to":…}` or `{"error":…}` |
| `/admin/updates/status` | `{"version":…,"run":{"state","from","to","message","finishedAt"}}`, polled by the progress dialog |
| `/admin/forms`, `/admin/forms/new`, `/admin/forms/<id>`, `/<id>/delete` | Forms: list, editor; create (POST `/admin/forms`), save, delete |
| `/admin/forms/<id>/submissions?q=&show=unread`, `/submissions/<id>`, `/submissions/bulk` (POST), `/export` | Answers: list, one answer (marks it read), mark read/new or delete, CSV |
| `/admin/mail`, `/admin/mail/test`, `/admin/mail/retry` (POST) | Mail overview; a test email to yourself; retry failed emails |
| `/admin/mail/lists`, `/admin/mail/lists/<id>?q=&status=`, `/<id>/subscribers` (POST), `/<id>/subscribers/<sid>` (POST), `/<id>/export`, `/<id>/delete` | Mailing lists and subscribers |
| `/admin/mail/send?list=&template=`, `/admin/mail/sent?status=` | Compose and send to a list (POST, `action=test\|send`); campaigns and the outbox |
| `/admin/mail/templates`, `/templates/new`, `/templates/<id>`, `/<id>/delete`; `/admin/mail/system`, `/system/<key>`, `/system/<key>/reset` | Templates and system emails |
| `/admin/audit?q=&area=&from=&to=` | Audit log (`q` also matches IP addresses) |
| `/admin/analytics?range=&from=&to=&page=&entry=&source=&channel=&referrer=&utm_source=&utm_medium=&utm_campaign=&country=&device=&browser=&os=` | Analytics (`range`: `today`, `yesterday`, `7d`, `30d`, `month`, `lastmonth`, `12mo`, `custom` with `from`/`to` as `yyyy-MM-dd`; a filter value `(none)` means empty). `/realtime`, `/export?dim=…` (CSV), `/settings` and `/delete` (POST) |

---
title: Security
nav_order: 7
---

# Security model
{: .no_toc }

1. TOC
{:toc}

## Admin sign-in and sessions

- **Passwords:** ASP.NET Core Identity (PBKDF2), at least 10 characters. After 5 wrong passwords the account is
  locked for 10 minutes; sign-in requests are limited to 10 per minute per IP address. Every failure shows the same
  message; failed sign-ins are in the audit log with the address.
- **Cookie:** `hp_admin`, HttpOnly, SameSite=Strict, path `/admin` (public pages never receive it), `Secure` on HTTPS,
  8-hour sliding expiry. It is encrypted with the Data Protection keys in `data/keys/`.
- **Permissions are not in the cookie.** They are resolved from the database on every request (cached per user until
  any user, group, role or grant changes), so revoking a role or making a user invalid takes effect on the next click.
- **CSRF:** every admin form and htmx request carries an antiforgery token (cookie `hp_af`, path `/admin`, SameSite
  Strict); requests without it are refused. The token is never on public pages, so they can be cached.
- Two-factor sign-in is not available yet.

## Authorization

- **Every action is checked on the server**, against the item's group and the user's effective permissions (see
  [Permissions](permissions.md)). The admin hides what you cannot do, but hiding is not the protection.
- **Lists are filtered in SQL** by the groups you can see; an item in another group is reported as not found, not as
  forbidden, so its existence does not leak.
- **Every editor operation** checks the item, the block type (the HTML block needs `html: rw`, also for moving or
  deleting it) and the nesting rules, then validates the whole tree before saving.

## Content

- Block settings are typed values. Text is HTML-encoded when rendered. Theme colors are only accepted as known
  token names.
- **Markdown** is rendered with Markdig and then sanitized with HtmlSanitizer: no scripts, event handlers or `javascript:`
  URLs. The **HTML block** is sanitized the same way.
- **Links** in buttons and menus must be site paths, `#anchors`, `http(s):`, `mailto:` or `tel:`.
- **Custom CSS** (themes: rw) cannot `@import`, cannot load `url()`s from other sites and cannot close its style
  element.

## Uploads

- The type is taken from the file's first bytes; only PNG, JPEG, GIF, WebP, AVIF, ICO and PDF are accepted (no
  SVG, no HTML). The name on disk is random; the original name is only shown in the admin.
- Media responses send the real type, `nosniff`, and `Content-Security-Policy: default-src 'none'; style-src
  'unsafe-inline'; sandbox`, so even a disguised file cannot run script in the site's origin.
- 20 MiB per file.

## Browser headers

| Header | Public site | Admin |
|---|---|---|
| `Content-Security-Policy` | `default-src 'self'; img-src 'self' data: https:; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-src https://www.youtube-nocookie.com https://player.vimeo.com; frame-ancestors 'none'; form-action 'self'; base-uri 'self'; object-src 'none'` | the same idea with `script-src 'self' 'unsafe-eval'` (Alpine.js evaluates its attributes) and `frame-ancestors 'self'` (the editor canvas) |
| `X-Frame-Options` | `DENY` | `SAMEORIGIN` |
| `X-Content-Type-Options` | `nosniff` | `nosniff` |
| `Referrer-Policy` | `strict-origin-when-cross-origin` | `strict-origin-when-cross-origin` |
| `Cache-Control` | output-cached pages | `no-store` |

There are no inline scripts or inline event handlers anywhere. The browser libraries (htmx, Alpine.js, SortableJS) are
part of the release, pinned, with their SHA-256 in `Homepage.Web/wwwroot/lib/VERSIONS.md`; nothing is loaded from a
CDN.

## Server

- **systemd hardening:** runs as `homepage`, `NoNewPrivileges`, `ProtectSystem=strict` with only `data/` writable,
  `ProtectHome`, `PrivateTmp`, `PrivateDevices`, `ProtectProc=invisible`, kernel and namespace protections, only
  `CAP_NET_BIND_SERVICE`, `UMask=0077`.
- **Program files** in `releases/` and `config/` are owned by root and read-only for the service.
- **Self-update never runs as the service.** The server only stages a package; a root-owned `update.sh` started by
  a systemd path unit checks it again, unpacks it safely, takes a `pg_dump` and rolls back if the new version does
  not start. Root scripts are never replaced from a package the server downloaded (see
  [Updates](server/updates.md#refreshing-updatesh-backupsh-and-the-units)).
- **Secrets on the command line:** none. The installer reads passwords from a mode-600 file, stdin, the environment
  or a hidden prompt.
- **Backups** are root-only (mode 600, `backups/` 700) and contain the Data Protection keys: whoever has a backup can
  forge an admin cookie. Store copies accordingly.
- **Behind a proxy:** enable `Site:ForwardedHeaders` only for proxies you control (loopback is always trusted;
  add others under `KnownProxies` / `KnownNetworks`). Block `/healthz` in the proxy.

## Known limits

{: .warning }
Releases are not signed yet: `SHA256SUMS.txt` catches corrupted downloads, not a compromised GitHub repository or
account. Signing `SHA256SUMS.txt` (minisign or cosign) with a pinned key is planned.

- No two-factor sign-in.
- The audit log records who did what, but not the old and new content of a page; published revisions cover that for
  content.

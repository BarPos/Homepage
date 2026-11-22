---
title: Security
nav_order: 7
---

# Security model
{: .no_toc }

1. TOC
{:toc}

## Admin sign-in and sessions

- **Passwords:** ASP.NET Core Identity (PBKDF2), at least 10 characters.
- **Failed sign-ins** are limited without locking accounts (a lockout would let anyone keep an admin out). Within
  15 minutes: 5 failures per account from one network, 20 per network, 50 per account from all networks; IPv6
  addresses count per /64. A slot is reserved before the password is checked, so parallel guesses get no extra tries.
  Sign-in requests are also limited to 10 per minute per network.
- **No user enumeration:** an unknown user name costs the same password hash as a real one, and every failure shows
  the same message. Note that posts show their author's **display name**, which is the user name until
  someone changes it: set a display name (Your profile) if the login name should stay private.
  The audit log records failures under the account name, or `(unknown user)`, never the typed text
  (often a password typed into the wrong field).
- **Cookie:** `hp_admin`, HttpOnly, SameSite=Strict, path `/admin` (public pages never receive it), `Secure` on HTTPS,
  8-hour sliding expiry and at most 12 hours after sign-in. It is encrypted with the Data Protection keys in `data/keys/`.
- **Sessions end** when the password is reset (admin or `--reset-local-password`) or the account is made invalid: the
  cookie carries the account's security stamp, which these change, and every request compares it.
- **Permissions are not in the cookie.** They are resolved from the database on every request (cached per user until
  any user, group, role or grant changes), so revoking a role or making a user invalid takes effect on the next click.
- **CSRF:** every admin request that changes something (any method but GET/HEAD) must carry the antiforgery token
  (cookie `hp_af`, path `/admin`, SameSite Strict), whether or not it sends form fields; requests without it are
  refused with 400. The token is never on public pages, so they can be cached.
- **Status messages** ("Saved.", or why something was refused) are never taken from the URL. They travel in a
  two-minute cookie protected with the Data Protection keys and are read once, so a crafted link cannot make the
  admin show text of someone else's choosing.
- **Confirmations** use an in-page dialog; destructive actions start with the focus on *Cancel*.
- **Two-factor sign-in** (optional, per account): TOTP codes (RFC 6238) from an authenticator app, or single-use
  recovery codes (stored hashed). After the password, a 5-minute cookie protected with the Data Protection keys
  remembers who is signing in; the session cookie is issued only after the code. Codes count against the same
  sign-in limits. Turning it on or off, and new recovery codes, change the security stamp: other sessions end.
  `--reset-local-password` turns it off.

## Authorization

- **Every action is checked on the server**, against the item's group and the user's effective permissions (see
  [Permissions](permissions.md)). The admin hides what you cannot do, but hiding is not the protection.
- **System groups never hold content:** creating, moving or uploading into them is refused, so `rw` on `themes`,
  `layout` or `html` never turns into the right to publish pages.
- **Media in blocks:** an Image block can only be pointed at a file the user can see (`ro` on its group); unknown ids
  are refused.
- **Published items:** changing the title, address, excerpt or blog of a published item is live at once, so it needs
  `publish`.
- **Lists are filtered in SQL** by the groups you can see; an item in another group is reported as not found, not as
  forbidden, so its existence does not leak.
- **Every editor operation** checks the item, the block type (the HTML block needs `html: rw`, also for moving or
  deleting it) and the nesting rules, then validates the whole tree before saving.

## Content

- Block settings are typed values. Text is HTML-encoded when rendered. Theme colors are only accepted as known
  token names.
- **Markdown** is rendered with Markdig and then sanitized with HtmlSanitizer: no scripts, event handlers or `javascript:`
  URLs, no forms or form controls (a hidden form on a public page could otherwise turn an admin's click into an
  admin action) and no positioning that could lay an invisible overlay over the page. The **HTML block** is sanitized
  the same way.
- **Links** in buttons and menus must be site paths, `#anchors`, `http(s):`, `mailto:` or `tel:`.
- **Custom CSS** (themes: rw) cannot load anything from other sites: CSS escapes and comments are removed first (so
  `u\72l(` or `@im/**/port` cannot hide a keyword), then `@import` goes, `image-set()` is made invalid and `url()` keeps
  only site paths and `data:` images, repeated until nothing changes. It cannot close its style element either.
- **The public cache** stores a full page and an htmx fragment separately, and only the exact header
  `HX-Request: true` counts as htmx, so no request can make the cache serve a fragment as a page.

## Page options

- A page's custom CSS needs `themes: rw` and is sanitized like a theme's: no `<`, no `@import`, no URLs to other
  sites, so it cannot close its style element or load anything.
- Theme, layout, CSS and no-index of a published page are live at once, so changing them needs `publish`.

## Uploads

- The type is taken from the file's first bytes; only PNG, JPEG, GIF, WebP, AVIF, ICO, SVG and PDF are accepted (no
  HTML). The name on disk is random; the original name is only shown in the admin.
- SVG images are parsed before they are stored (no DTDs, an `svg` root, SVG elements only) and refused when they
  contain scripts, event handlers, embedded HTML or frames, links to other files, or styles that load other files.
- Media responses send the real type, `nosniff`, and `Content-Security-Policy: default-src 'none'; style-src
  'unsafe-inline'; sandbox`, so even a disguised file cannot run script in the site's origin.
- 20 MiB per file.

## Analytics and privacy

- Visits are counted **without cookies** and without storing addresses: a visitor is an HMAC-SHA-256 of address,
  browser and site with a random salt that changes daily and is deleted the day after (see
  [Analytics](admin/analytics.md#how-visitors-are-counted)). Country lookup uses a database on the server.
- `/hp/event` takes only small JSON messages (4 KB) from pages of the same host (`Origin`/`Referer`), is rate limited
  per network, drops bots, admin paths and anything malformed, and queues hits in memory (a full queue drops them,
  so a flood cannot slow the site). Like any analytics, someone can still send made-up page views.
- Reports and CSV exports need `analytics: ro`; dimensions and filters come from a fixed list, never from SQL text.
  Exported values that start with `=`, `+`, `-` or `@` are quoted so spreadsheets do not run them as formulas.

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
- **Self-update never runs as the service, and never trusts what the service staged.** The server only stages a
  package. The root-owned `update.sh`:
  - reads the staged files as the service user (links, swaps and FIFOs gain nothing root could read), with size caps
    and timeouts;
  - downloads the release's `SHA256SUMS.txt` from `github.com/<GITHUB_REPOSITORY>` itself and installs the package only
    if it matches. A compromised server process therefore cannot install a program of its own, only real, newer
    releases;
  - unpacks it safely, takes a `pg_dump` and rolls back if the new version does not start.

  Root scripts are never replaced from a package the server downloaded (see
  [Updates](server/updates.md#refreshing-updatesh-backupsh-and-the-units)).
- **Release programs run sandboxed** even outside the service: migrations during an update and the installer's
  account commands run under `systemd-run` with the same restrictions as `homepage.service`, in their own session
  (no access to root's terminal or open files).
- **Secrets on the command line:** none. The installer reads passwords from a mode-600 file, stdin, the environment
  or a hidden prompt.
- **Backups** are root-only (mode 600, `backups/` 700) and contain the Data Protection keys: whoever has a backup can
  forge an admin cookie. Store copies accordingly. A backup includes only what a restore accepts (folders and regular,
  single-link files without setuid/setgid); anything else in `data/` is left out and listed, so planted files cannot
  make backups unrestorable. Sparse files are stored sparse.
- **Behind a proxy:** with `--mode=http` on loopback the installer enables `Site:ForwardedHeaders` (only loopback is
  trusted). For a proxy on another machine, list it under `KnownProxies` / `KnownNetworks`, and make sure it overwrites
  `X-Forwarded-For` (Caddy does; nginx needs `proxy_set_header X-Forwarded-For $remote_addr;`). `/healthz` answers
  only its exact path; block it in the proxy as well.

## Known limits

{: .warning }
Releases are not signed yet. `update.sh` checks the package against the `SHA256SUMS.txt` published on GitHub, which
stops a compromised server process, but not a compromised GitHub repository or account. Signing `SHA256SUMS.txt`
(minisign or cosign) with a pinned key is planned.

- Self-update verification needs a public release repository (root downloads `SHA256SUMS.txt` without a token).
- The audit log records who did what and from which address (the request's IP, and the IP the session was signed in
  from when it differs), but not the old and new content of a page; published revisions cover that for content.
  Behind a proxy the addresses are only right with `Site:ForwardedHeaders` enabled.

---
title: Troubleshooting
nav_order: 10
---

# Troubleshooting
{: .no_toc }

1. TOC
{:toc}

## Where to look

| | |
|---|---|
| Server log | `journalctl -u homepage -f` |
| Self-update log | `journalctl -u homepage-update` |
| Admin | **Audit log**, **Settings → About and updates** |
| Browser | the developer tools' console and network tab (the editor reports failed changes there and in the corner) |

## The service does not start / systemd times out

- Check that the config is valid JSON and readable by `homepage`: `/opt/homepage/config/appsettings.Production.json`,
  mode 640, group `homepage`.
- Check that PostgreSQL is reachable with the connection string. The server applies migrations at start, which can
  take a while after an update (`TimeoutStartSec=300`).
- *address already in use*: something else listens on the site port (`ss -ltnp`). Change `Site:Urls` and `HEALTH_URL`
  in `update.env`, or stop the other program.
- `https` mode: the certificate or key in `Site:Certificate` is missing, unreadable by `homepage` or not PEM.
- Run `sudo ./install.sh --repair` to restore the units, owners and modes.

## The installer stops at the database step

- *cannot connect to 127.0.0.1:PORT as 'homepage' with a password*: the machine's PostgreSQL does not allow password
  logins from localhost. The installer has already removed the role and database it created. Add the line it shows to
  `pg_hba.conf` (for example `host homepage homepage 127.0.0.1/32 scram-sha-256`), reload PostgreSQL
  (`systemctl reload postgresql`) and run the installer again. It never edits `pg_hba.conf` itself.
- *the database 'X' already exists* or *the role 'X' already exists*: a name given with `--db-name` / `--db-user` is
  taken. Homepage never takes over existing roles or databases: choose another name, or leave both options out to get
  the first free one of `homepage`, `homepage_2`, …
- *several PostgreSQL clusters are running*: choose one with `--db-port` (see `pg_lsclusters`).
- *PostgreSQL 16 or newer is required*: the machine's cluster is older. The installer does not upgrade it; upgrade it
  yourself, or use `--db=existing` with a PostgreSQL 16+ server elsewhere.
- *port 5432 is in use, but there is no PostgreSQL cluster from Ubuntu's packages*: something else (for example a
  PostgreSQL in a container) uses the port. Make a role and database for Homepage on it and install with
  `--db=existing`.

## The site is not reachable behind Caddy or nginx

- `curl -s http://127.0.0.1:5080/healthz` on the server must answer `{"status":"ok",…}`. If not, look at the service.
- The proxy must forward to the same address and port as `Site:Urls`.
- The audit log shows `127.0.0.1` for every sign-in, sign-in limits hit everyone at once, or RSS and sitemap links
  start with `http://`: enable `Site:ForwardedHeaders:Enabled`, so the server sees the visitor's address and the
  HTTPS scheme (and marks the admin cookie `Secure`). For a proxy on another machine, add its address to
  `KnownProxies`. Setting the public address in **Settings** also fixes the links.
- `/healthz` answering the internet: block it in the proxy (see [Installation](server/installation.md#http-or-https)).

## Cannot sign in

- *Wrong user name or password, or the account is locked*: after 5 wrong passwords the account is locked for 10
  minutes. The audit log shows `signin.failed` entries with the address.
- *This account has no permissions yet*: the account exists but has no role or grant. Give it one under **Users ↔
  Roles**.
- *This account has no access to the admin area* after signing in: the account was made invalid, or its last role was
  removed.
- *429 Too Many Requests*: more than 10 sign-in attempts per minute from your address. Wait a minute.
- Every admin is locked out: `sudo bash install.sh --reset-password`.

## A page, button or menu is missing in the admin

That is the permission model at work. **Users & permissions → Users → Edit** shows the user's effective permissions.
Common cases:
- Menus need `layout: rw`; the theme editor needs `themes: rw`; the HTML block needs `html: rw`; Users and Settings
  need `admin: rw`; the audit log needs `audit: ro`.
- An author cannot edit a post any more: it was published, and `create` covers only items that were never published.
- *Not found* for a page that exists: it is in a group the user has no permission on.

## The editor shows an error

- *'X' cannot contain 'Y'* / *cannot be placed there*: the [nesting rules](admin/blocks.md#nesting-rules), for
  example a section inside a section, or a column outside columns.
- *You may not use the 'HTML' block*: it needs `html: rw`.
- *Someone else saved this page at the same time*: another editor changed it; the editor reloaded their version. Make
  your change again.
- *Built-in themes are read-only*: duplicate the theme to edit its header, footer or design.
- The canvas does not react to dragging: reload the page. Dragging is mouse- and touch-based; very fast flicks are
  ignored.

## A published page is not reachable

- Check its address in the item settings. A page can never take `admin`, `blog`, `search`, `media`, `theme` or the
  other site addresses: a page titled *Blog* gets `/blog-2`, and the settings refuse such an address.
- Pages are at `/<slug>`, posts at `/blog/<slug>`.
- Is it still published? *Draft* in the list means it was unpublished.

## The site still shows the old design or text

- Pages are cached for up to 30 minutes, but publishing and every theme, menu or settings change empty the cache at
  once. A change that did not reach the site was probably not saved (look for an error in the editor or theme editor).
- Theme CSS and media are cached by browsers for a year under URLs that change with every save; a hard reload is
  never needed for them.

## Upload refused

- *Only PNG, JPEG, GIF, WebP, AVIF images and PDF files can be uploaded*: the file's content is something else (often
  SVG or HEIC). Convert it.
- *Files can be at most 20 MiB*.
- *You cannot upload into that group*: you need `create` on the group.

## Update failed or rolled back

1. `journalctl -u homepage-update` shows each step of `update.sh` and the reason.
2. **Settings → About and updates** shows the last run's state and message.
3. A rollback restores the database dump from `/opt/homepage/backups/pre-update/` and starts the old version. Nothing
   needs to be done to keep running; fix the cause (often a database that ran out of disk space, or a migration
   error in the log) and try again.
4. *Self-update needs Ubuntu 24.04 LTS or newer* / *not installed with install.sh*: update with
   `install.sh --upgrade --package FILE` instead.

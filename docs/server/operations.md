---
title: Operations
parent: Server
nav_order: 4
---

# Operations

## Service

```sh
sudo systemctl status homepage
sudo systemctl restart homepage
journalctl -u homepage -f              # server log
journalctl -u homepage-update          # self-update log
```

The unit runs as `homepage`, with `NoNewPrivileges`, `ProtectSystem=strict` (writable: `data/` only),
`ProtectHome`, `PrivateTmp`, `ProtectProc=invisible`, a restricted set of address families, and
`CAP_NET_BIND_SERVICE` only, so it can listen on port 443.

## Command line

Run these as the service user, with the configuration:

```sh
cd /opt/homepage/current
sudo -u homepage env HOMEPAGE_CONFIG=/opt/homepage/config/appsettings.Production.json \
     ASPNETCORE_ENVIRONMENT=Production ./Homepage.Server <option>
```

| Option | |
|---|---|
| `--migrate-only` | Apply pending database migrations, then content migrations, and exit (also done at every start) |
| `--create-local-account NAME` | Create an account with `admin: rw`; the password is read from stdin (first line, at least 10 characters) |
| `--reset-local-password NAME` | Set a new password for an account and make it valid; the password is read from stdin. `install.sh --reset-password` wraps this. |
| `--list-local-accounts` | Print the accounts (`name<TAB>enabled\|disabled`) |

Logs of these commands go to stderr; stdout carries only their own output.

## Health

`GET /healthz` returns `{"status":"ok","version":"…"}` to requests from **loopback only**. Other clients get 404.
`update.sh` uses it. Behind a proxy, block the path in the proxy too (see
[Installation](installation.md#http-or-https)).

## Caching

- **Public pages** are kept in memory for up to 30 minutes and served without database queries. Publishing,
  unpublishing or deleting content, deleting media, and changing the theme, menus, blogs or settings, empties the
  cache at once.
- **Theme CSS** (`/theme/<id>/<version>.css`) and **media** (`/media/<file>`) are sent with a one-year immutable
  cache header. A changed theme gets a new URL; a media file never changes.
- **The admin** is never cached (`Cache-Control: no-store`).

## Backup and restore

Backup, restore and update rollbacks work only on Homepage's own database, as its own role. On a PostgreSQL shared
with other applications, their databases are neither included nor touched.

`/opt/homepage/bin/backup.sh` (also available as `install.sh --backup` / `--restore`) puts everything in one archive:
- the **database**: a `pg_dump` of Homepage's own database only, made as its role: pages, posts, revisions,
  themes, menus, accounts, permissions, settings and the audit log;
- the **configuration**: `config/`, including the certificate;
- the **data**: `data/`, including **all uploaded media** and the Data Protection keys;
- the **running program**: `releases/<version>/`.

`bin/` (`update.sh`, `backup.sh`) and the systemd units run as root and are not part of a backup: a restore always
keeps the ones installed on the server.

```sh
sudo /opt/homepage/bin/backup.sh create                         # → /opt/homepage/backups/homepage-backup-<version>-<host>-<time>.tar.gz
sudo /opt/homepage/bin/backup.sh create --output /mnt/nas/      # a folder, or a file name ending in .tar.gz
sudo /opt/homepage/bin/backup.sh list
sudo /opt/homepage/bin/backup.sh restore /mnt/nas/homepage-backup-1.0.2-web-20261116T020000Z.tar.gz
```

- **Where:** `/opt/homepage/backups/` is root-only (root:root 700). An `--output` folder must be one that only
  root can change: it and every folder above it owned by root and not writable by group or others, and not a
  symbolic link. `/tmp`, or a folder owned by another user, is refused. The archive is built in a new private
  folder inside the destination and then renamed into place.
- **Restore** replaces the tables in Homepage's own database, the configuration, data and program with the backup's,
  and makes the backup's version the running one. It first checks the whole archive and refuses it if anything is
  wrong: paths outside the expected folders, hard links, devices, FIFOs, setuid or setgid entries, links pointing
  outside the backup, and any link in `config/`. Then it writes a backup of the current state (to
  `/opt/homepage/backups/`), asks you to type `restore` (or `--yes`), starts the server and waits for it to answer.
- **Moving to a new server:** first install Homepage there with `install.sh`, from the same version as the backup
  (this installs `update.sh`, `backup.sh` and the units from a verified package), then restore. The new server's
  database connection settings are kept, so a backup from another server works. Point DNS at the new server.
  Signed-in users stay signed in, because the Data Protection keys come from the backup.
- **Secrets:** archives contain the database password and the Data Protection keys (anyone with them can forge a
  sign-in cookie). They are written with mode 600, owned by root. Keep copies off the server, for example in a
  nightly cron job: `0 3 * * * root /opt/homepage/bin/backup.sh create --output /mnt/nas/homepage/` (the mount
  point must be owned by root and not writable by others).
- **What is left out:** only folders and regular files with a single link and no setuid/setgid bit are archived
  (what a restore accepts). Anything else under `data/` (links, FIFOs, sockets, devices, hard links) is left out and
  listed as a warning; Homepage itself never creates such files there.
- **Before updates:** `update.sh` also writes a database dump to `/opt/homepage/backups/pre-update/` before every
  update.

## Firewall

| Port | From | |
|---|---|---|
| 80/tcp, 443/tcp | Visitors | The reverse proxy, or Kestrel itself in `https` mode |
| 5080/tcp | Nobody | Kestrel in `http` mode listens on 127.0.0.1 only |
| Outbound 443 | to `github.com`, `api.github.com`, `objects.githubusercontent.com` | Only when updates are configured |

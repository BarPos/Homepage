---
title: Installation
parent: Server
nav_order: 1
---

# Server installation
{: .no_toc }

1. TOC
{:toc}

`install.sh` installs Homepage on **Ubuntu 24.04 LTS or newer** (x64 or arm64). It uses no Docker and installs no
.NET runtime, because the server is published self-contained. Run it as root.

## Interactive install

```sh
curl -fsSLO https://github.com/BarPos/Homepage/releases/latest/download/install.sh
sudo bash install.sh                       # downloads the latest release for this CPU
sudo bash install.sh --version 1.2.0       # a specific release
sudo bash install.sh --package Homepage.Server-1.2.0-linux-x64.tar.gz   # offline, from a file
```

The script downloads from the releases of `BarPos/Homepage` (`GITHUB_REPOSITORY` at the top of the script) and checks
each download against the release's `SHA256SUMS.txt`.

## Unattended install

```sh
sudo HOMEPAGE_ADMIN_PASSWORD='…' ./install.sh --unattended --db=local \
     --package Homepage.Server-1.0.0-linux-x64.tar.gz \
     --mode=http --server-name www.example.com --admin-user admin
```

| Option | Meaning |
|---|---|
| `--package FILE` | A `Homepage.Server-<version>-linux-<arch>.tar.gz` (checked against `SHA256SUMS.txt` next to it) |
| `--version X.Y.Z` | Download that release from GitHub |
| `--db=local` | PostgreSQL on this machine: installed when there is none, shared when there is one (see below). Homepage gets its own new role and database. `--db-port` picks the cluster when several run; `--db-name` / `--db-user` set the names (they must be free) |
| `--db=existing` | Use an existing PostgreSQL: `--db-host`, `--db-port`, `--db-name`, `--db-user`, `--db-sslmode` |
| `--mode=http\|https` | `http`: plain HTTP for a reverse proxy on this machine. `https` (default): Kestrel serves HTTPS with a certificate. See [HTTP or HTTPS](#http-or-https). |
| `--port N` | Site port (default 5080 for `http`, 443 for `https`) |
| `--bind ADDRESS` | Address to listen on: `127.0.0.1`, `0.0.0.0`, `::1` or `::` (default `127.0.0.1` for `http`, `0.0.0.0` for `https`) |
| `--server-name NAME` | DNS name used by browsers (default: `hostname -f`) |
| `--cert`, `--cert-key` | `https` mode: an existing PEM certificate and key; without them a self-signed one is generated |
| `--admin-user NAME` | First account; it gets `admin: rw` |
| `--open-firewall=yes\|no` | Open the site port when ufw is active and the site is not bound to loopback |
| `--secrets-file FILE` / `--secrets-stdin` | `KEY=VALUE` lines: `ADMIN_PASSWORD`, `DB_PASSWORD` (the file must be mode 600) |

Passwords are **never** accepted as command-line flags, because other users can read the process list. They come
from `--secrets-file`, `--secrets-stdin`, the environment (`HOMEPAGE_ADMIN_PASSWORD`, `HOMEPAGE_DB_PASSWORD`) or a
hidden prompt. The admin password needs at least 10 characters.

## HTTP or HTTPS

| Mode | Kestrel listens on | Certificate | Use it when |
|---|---|---|---|
| `http` | `http://127.0.0.1:5080` | none; the proxy has it | **A public website.** Caddy or nginx on the same machine gets certificates from Let's Encrypt and renews them. |
| `https` | `https://0.0.0.0:443` | the PEM files you give, or a self-signed one | An internal site, or when you renew and copy the certificate yourself |

With `http` on loopback, the installer prints a Caddy block for your server name at the end:

```
www.example.com {
    @health path /healthz /healthz/*
    respond @health 404
    reverse_proxy 127.0.0.1:5080
}
```

Bound to loopback, the installer also enables forwarded headers (see [Configuration](configuration.md#site)), so the
server sees the visitor's address and scheme.

{: .warning }
`https` mode has **no automatic renewal**. A certificate copied into `config/` expires unless you replace it and
restart the service (for example from a certbot deploy hook).

{: .important }
`/healthz` answers only requests from loopback, and `update.sh` uses it to decide whether an update worked. Behind a
proxy on the same machine, every proxied request comes from 127.0.0.1, so enable forwarded headers **and** block
`/healthz` in the proxy, as in the block above.

To switch an existing install:
1. Change `Site:Urls` (and `Site:Certificate`) in `/opt/homepage/config/appsettings.Production.json`.
2. Change `HEALTH_URL` in `/opt/homepage/config/update.env`.
3. `sudo systemctl restart homepage`.

## Sharing PostgreSQL with other applications

With `--db=local`, a PostgreSQL that is already installed on the machine (from Ubuntu's or the PostgreSQL project's
packages) may keep serving other applications. Homepage:

- **only adds** a new role and a new database. They are called `homepage`, or `homepage_2`, `homepage_3`, … when a
  role or database of that name already exists. An existing role or database is never altered or taken over. A name
  given with `--db-name` / `--db-user` that is taken stops the install.
- creates the role without superuser, database or role creation rights, and allows only that role (and superusers) to
  connect to its database.
- never installs, upgrades, reconfigures (`postgresql.conf`, `pg_hba.conf`) or restarts PostgreSQL, and does not
  upgrade packages that are already installed. A cluster older than PostgreSQL 16 is refused, not upgraded.
- uses the cluster on port 5432, or the only running one; with several, choose one with `--db-port`.
- connects with a password over `127.0.0.1`. If the cluster's `pg_hba.conf` does not allow that, the install stops,
  removes the role and database it just created, and shows the line to add.
- when an install fails later on, removes the role and database that run created, and nothing else.

Backups, restores and update rollbacks work only inside Homepage's own database (as its role). `--uninstall` drops
only that database and that role, after checking that the database belongs to the role and the role is not a
superuser. PostgreSQL itself, its packages and every other database and role stay as they are.

### Using an existing PostgreSQL

- The server must run **PostgreSQL 16 or newer**.
- If it is newer than 16, the script adds the PostgreSQL apt repository and installs the matching `postgresql-client`
  (needed for `pg_dump` backups).
- The role must **own** the database, so that an update can restore a backup on rollback.

## What gets installed

| Path | Owner / mode | Content |
|---|---|---|
| `/opt/homepage/releases/<version>/` | root, 755 | Program files |
| `/opt/homepage/current` | symlink | The running release |
| `/opt/homepage/config/appsettings.Production.json` | root:homepage 640 | [Configuration](configuration.md) |
| `/opt/homepage/config/site.pem`, `site.key` | root:homepage 640 | Certificate (`https` mode only) |
| `/opt/homepage/config/update.env` | root 600 | Database settings and `HEALTH_URL` for `update.sh` |
| `/opt/homepage/config/install.state` | root 600 | The choices the installer made (mode, port, database names) |
| `/opt/homepage/data/` | homepage 700 | `media/` (uploads), `keys/` (Data Protection keys: sign-in cookies), `updates/` (staged updates), `themes/` |
| `/opt/homepage/backups/` | root 700 | Archives from `backup.sh`; `pre-update/` holds the `pg_dump` taken before each update. The server cannot read or write it. |
| `/opt/homepage/bin/update.sh`, `backup.sh` | root 755 | The updater and the backup script (replaced only by `install.sh` from a package you give it, see [Updates](updates.md#refreshing-updatesh-backupsh-and-the-units)) |
| `/etc/systemd/system/homepage.service` | | The server (`Type=notify`, `User=homepage`, hardened) |
| `/etc/systemd/system/homepage-update.{path,service}` | | Applies a staged self-update as root |

The installer also installs these apt packages: `ca-certificates`, `curl`, `tar`, `openssl`, `tzdata`, the ICU and
OpenSSL libraries, `libgssapi-krb5-2` and `postgresql-client`.

Logs go to the journal: `journalctl -u homepage -f`.

## Maintenance: repair, upgrade, backup, restore, password reset, uninstall

Running `sudo ./install.sh` on an installed server shows a menu:
1. repair;
2. upgrade;
3. backup;
4. restore a backup;
5. reset a local account's password;
6. uninstall;
7. cancel.

Each item also has a flag:

| Command | Effect |
|---|---|
| `install.sh --repair` | Re-applies owners and modes and restarts the services; never overwrites config or data. Keeps the installed `update.sh`, `backup.sh` and units; with `--package FILE` or `--version X.Y.Z` it reinstalls them, and the program, from that package (same or newer version) |
| `install.sh --upgrade --package FILE` | Installs a newer release through the same path as the admin's *Update now* |
| `install.sh --backup [--output PATH]` | Backs up the database, configuration, data (media, keys) and the running program (see [Backup and restore](operations.md#backup-and-restore)) |
| `install.sh --restore FILE` | Restores a backup on this server (asks for confirmation; `--yes` when unattended) |
| `install.sh --reset-password [USER]` | Sets a new password for an account and makes it valid. Without `USER` it lists the accounts and asks. Unattended: `HOMEPAGE_ADMIN_PASSWORD` or `--secrets-file`. |
| `install.sh --uninstall` | Removes **everything**: program, services, `/opt/homepage` (config, media, keys, backups), the service user **and the database** |
| `install.sh --uninstall --keep-data` | Removes only the program and its services; keeps config, data, backups and the database |

{: .warning }
`--uninstall` deletes the database with every page, post, revision and account, and all uploaded media. The
installer warns, offers to write a backup to a file outside `/opt/homepage` first, and asks you to type `uninstall`.
Unattended runs need `--yes`, and can write the backup with `--backup-to FILE`. A local database is dropped together
with its role, and nothing else on that PostgreSQL. For an existing (external) database, every table Homepage created
is dropped; the database and role stay. `--purge` is an alias of `--uninstall`.

## After installing

1. Sign in at `/admin` with the first account.
2. **Settings:** site title, tagline and the public address.
3. Edit the home page, pick a theme, and add users (see [Getting started](../getting-started.md)).

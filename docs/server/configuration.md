---
title: Configuration
parent: Server
nav_order: 2
---

# Configuration
{: .no_toc }

1. TOC
{:toc}

The server reads, in this order (later wins):
1. `appsettings.json` next to the program;
2. `appsettings.Production.json` from the file named by `HOMEPAGE_CONFIG` (installs: `/opt/homepage/config/appsettings.Production.json`);
3. environment variables with the `HOMEPAGE_` prefix, `__` between levels (for example `HOMEPAGE_Updates__Channel=prerelease`).

Restart after a change: `sudo systemctl restart homepage`. Settings an administrator changes in the admin are kept in
the database instead (see [Settings in the database](#settings-in-the-database)).

## Example

```jsonc
{
  "ConnectionStrings": { "Default": "Host=127.0.0.1;Port=5432;Database=homepage;Username=homepage;Password=…;SSL Mode=Disable" },
  "Site":    { "Urls": "http://127.0.0.1:5080", "Certificate": "", "CertificateKey": "",
               "ForwardedHeaders": { "Enabled": true, "KnownProxies": [], "KnownNetworks": [] } },
  "Paths":   { "Data": "/opt/homepage/data", "Backups": "/opt/homepage/backups" },
  "Updates": { "GitHubRepository": "BarPos/Homepage", "Channel": "stable", "CheckIntervalHours": 24, "GitHubToken": "" }
}
```

## ConnectionStrings

| Key | Default | |
|---|---|---|
| `Default` | — | Npgsql connection string. **PostgreSQL 16+ is required.** The file holding it is readable only by root and `homepage`. |

## Site

| Key | Default | |
|---|---|---|
| `Urls` | `http://127.0.0.1:5080` | Kestrel URLs, `;`-separated. `https://0.0.0.0:443` serves HTTPS itself (needs `Certificate`). IPv6: `http://[::]:5080`. |
| `Certificate` | — | PEM certificate (chain) for HTTPS. Not used for `http://` URLs. |
| `CertificateKey` | — | PEM private key |
| `ForwardedHeaders:Enabled` | `false` | Trust `X-Forwarded-For`, `-Proto` and `-Host` from a reverse proxy, so sign-in limits and the audit log see the visitor's address, and cookies are marked secure behind HTTPS. **Turn it on behind Caddy or nginx.** |
| `ForwardedHeaders:KnownProxies` | `[]` | IP addresses whose forwarded headers are trusted. Loopback is always trusted, so a proxy on the same machine needs nothing here. |
| `ForwardedHeaders:KnownNetworks` | `[]` | Networks whose forwarded headers are trusted (CIDR, e.g. `10.0.0.0/24`) |

The admin cookie is `Secure` when the request is HTTPS (directly, or as reported by a trusted proxy). Over plain HTTP
without a proxy, sign-in works but the password and cookie travel unencrypted: use that only on a trusted network.

## Paths

| Key | Default | |
|---|---|---|
| `Data` | `/opt/homepage/data` | Media uploads (`media/`), Data Protection keys (`keys/`), staged updates (`updates/`). The only folder the service may write. |
| `Backups` | `/opt/homepage/backups` | Database dumps taken by `update.sh` and archives from `backup.sh` (root only) |

Relative paths are resolved against the program folder.

## Updates

| Key | Default | |
|---|---|---|
| `GitHubRepository` | `BarPos/Homepage` | `owner/name` of the release repository. **Blank = updates off and no network calls at all.** |
| `Channel` | `stable` | `stable` = latest release; `prerelease` = newest release including prereleases |
| `CheckIntervalHours` | `24` | Release check interval |
| `GitHubToken` | — | Only for a private repository |

## Logging

Standard ASP.NET Core logging (`Logging:LogLevel`). The service logs to the journal: `journalctl -u homepage -f`.

## Settings in the database

These are changed in the admin and kept in the `settings` table, so they survive updates and are part of every backup.

| Where | Setting | |
|---|---|---|
| **Settings** | Site title, tagline | Shown in the header and in page titles |
| | Public address | `https://www.example.com`: used in RSS, the sitemap and canonical links. Empty = the address of the request. |
| | Home page | What `/` shows: a published page, or a blog's list of posts |
| | Logo, favicon | Images from Media. The logo is shown by the *Logo / site name* block and falls back to the title. |
| | Default group for new content | Preselected when someone creates a page or post |
| **Themes** | Active theme, design tokens, custom CSS, header and footer | See [Themes](../admin/themes.md) |
| **Menus** | Menus and their links | See [Media and menus](../admin/media-menus.md) |
| **Blogs** | Blogs, their addresses, descriptions and page sizes | See [Blogs](../admin/blogs.md) |
| **Users & permissions** | Accounts, groups, roles and grants | See [Permissions](../permissions.md) |

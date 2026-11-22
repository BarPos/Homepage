---
title: Updates
parent: Server
nav_order: 3
---

# Updates
{: .no_toc }

1. TOC
{:toc}

## Version rules

- One version number for the whole product: `<Version>` in `Directory.Build.props`, the same as `AppVersion.Current`.
- Versions are plain `x.y.z`; the release tag is `v<version>`.
- Updates only go forward. Downgrades are not offered; a [backup](operations.md#backup-and-restore) of an older
  version can be restored instead.
- Stored pages are migrated forward by the new version (see [Content migrations](../reference/content-format.md#content-migrations)).

## Release source

Updates come from the GitHub releases of the repository in `Updates:GitHubRepository`, which defaults to
[`BarPos/Homepage`](https://github.com/BarPos/Homepage/releases). The releases are built by the manually run
**release** workflow (see [Development](../development.md#releasing)). **Set the value to blank to turn updates off:
the server then makes no network calls**, and the update section of **Settings** says that updates are not configured.

Each release must carry these assets:

| Asset | |
|---|---|
| `Homepage.Server-<version>-linux-x64.tar.gz`, `-linux-arm64.tar.gz` | Server packages |
| `install.sh` | Installer |
| `SHA256SUMS.txt` | Hashes of all of the above |

{: .warning }
Releases are not signed yet. `SHA256SUMS.txt` catches corrupted downloads, not a compromised repository.

## Update now

**Settings → About and updates** shows the current version, the latest release with its notes, **Check now** and
**Update now** (admins only). It is supported on Ubuntu 24.04+ installs made by `install.sh`; elsewhere the page says
why it is not.

1. The server downloads the tarball for its CPU and `SHA256SUMS.txt`, verifies the hash and stages the files in
   `data/updates/<version>/`.
2. It writes `data/updates/request.conf`. The path unit `homepage-update.path` starts the one-shot
   `homepage-update.service`, which runs `update.sh` as root. The server itself never needs root.
3. `update.sh` does the following:
   - accepts only a plain `x.y.z` that is newer than the running version;
   - reads the staged files as the service user and checks the package against the `SHA256SUMS.txt` it downloads
     **itself** from release `v<version>` of the repository in `GITHUB_REPOSITORY` (in `config/update.env`, root only).
     The staged sums only prove the copy is intact; the published ones prove it is the real release;
   - unpacks safely: no absolute paths, no `..`, no links pointing outside, files owned by root;
   - stops the server and writes a `pg_dump` backup to `backups/pre-update/` (root only);
   - runs `--migrate-only` with the new version, as `homepage`: database migrations, then the content migrations of
     every stored page, post, revision and theme layout;
   - switches the `current` symlink and starts the server.
4. **Rollback:** if `/healthz` does not answer with the new version within 2 minutes, it restores the backup, switches
   the symlink back and starts the old version.
5. The result appears under **Settings** after the restart. The last 3 releases are kept.

**Update now** asks for confirmation, then shows a progress dialog. Keep the page open:

| Step | What happens |
|---|---|
| Downloading and checking the release | The server downloads the package and verifies its SHA-256 |
| Waiting for the updater | `homepage-update.path` starts `update.sh` (normally within seconds) |
| Backing up the database and restarting | The site is unreachable while `update.sh` dumps the database, migrates and switches versions |
| Starting the new version | The site answers again |

The dialog polls the server every two seconds. When it answers with the new version, the page reloads by itself and
**Settings** shows the finished run. If the update was **rolled back** or failed, the dialog says so with the reason,
and the old version keeps running. If the updater has not started after two minutes, the dialog says what to check on
the server; after 15 minutes it points at `journalctl -u homepage-update`. If the restart ended your session, you are
sent to the sign-in page and back to **Settings**.

While the server restarts, the site is unavailable for a few seconds. Public pages are served again as soon as it
answers; the output cache starts empty.

{: .important }
**Upgrading from 0.2.1 or older:** the verification above lives in the new `update.sh`, which an update through the
admin does not install (see below). After updating, refresh the root scripts once with
`sudo bash install.sh --repair --version <new version>`. This also adds `GITHUB_REPOSITORY` to `update.env`.

`install.sh --upgrade --package FILE` works offline: root approves exactly that package (a root-only file in
`config/`), so `update.sh` does not need GitHub for it.

### Refreshing update.sh, backup.sh and the units

`update.sh`, `backup.sh` and the systemd units run as root. *Update now* never replaces them: the new release's copies
are in `current/deploy/linux/`, but the package was staged by the server process, so nothing in it is trusted as
root. After an update through the admin, the root scripts and units stay at the version that `install.sh` last
installed. They are refreshed only from a package you give `install.sh`:

```sh
# get the install.sh of that release
curl -fsSLO https://github.com/BarPos/Homepage/releases/download/v1.2.0/install.sh
# the release that is now running, downloaded from GitHub and checked against its SHA256SUMS.txt
sudo bash install.sh --repair --version 1.2.0
# or offline, from the tarball (checked against SHA256SUMS.txt when it is next to it)
sudo bash install.sh --repair --package Homepage.Server-1.2.0-linux-x64.tar.gz
```

`install.sh --upgrade` refreshes them as well, from the package it installs. `install.sh --repair` with
`--package`/`--version` also reinstalls the program from that package and makes it the running release (never an
older one: restore a backup for that), which recovers a server from an unexpected release. Without
`--package`/`--version` it keeps the installed copies (it stops if one is missing). Run `install.sh` from a download
or a package you unpacked yourself, never from `/opt/homepage/current/` or `releases/`.

## Built-in themes

Built-in themes are read-only in the admin, so a release may restyle them: at start the server takes the new tokens
from the release and gives the theme a new CSS version. Themes you duplicated are never changed by an update.

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
   - accepts only a plain `x.y.z` that is newer than the running version, and checks the hash again;
   - unpacks safely: no absolute paths, no `..`, no links pointing outside, files owned by root;
   - stops the server and writes a `pg_dump` backup to `backups/pre-update/` (root only);
   - runs `--migrate-only` with the new version, as `homepage`: database migrations, then the content migrations of
     every stored page, post, revision and theme layout;
   - switches the `current` symlink and starts the server.
4. **Rollback:** if `/healthz` does not answer with the new version within 2 minutes, it restores the backup, switches
   the symlink back and starts the old version.
5. The result appears under **Settings** after the restart. The last 3 releases are kept.

While the server restarts, the site is unavailable for a few seconds. Public pages are served again as soon as it
answers; the output cache starts empty.

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

`install.sh --upgrade` refreshes them as well, from the package it installs. `install.sh --repair` without
`--package`/`--version` keeps the installed copies (it stops if one is missing). Run `install.sh` from a download
or a package you unpacked yourself, never from `/opt/homepage/current/` or `releases/`.

## Built-in themes

Built-in themes are read-only in the admin, so a release may restyle them: at start the server takes the new tokens
from the release and gives the theme a new CSS version. Themes you duplicated are never changed by an update.

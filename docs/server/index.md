---
title: Server
nav_order: 4
has_children: true
---

# Server

The server is a single self-contained Linux program, `/opt/homepage/current/Homepage.Server`, run by the systemd unit
`homepage.service` as the unprivileged user `homepage`. It contains:

- the **public site** and the **admin** on `Site:Urls` (`http://127.0.0.1:5080` behind a proxy, or
  `https://0.0.0.0:443`);
- the background jobs: release checks and recording the result of a self-update.

| Page | |
|---|---|
| [Installation](installation.md) | `install.sh`: interactive and unattended installs, database choice, HTTP or HTTPS, layout, repair, uninstall |
| [Configuration](configuration.md) | Every setting in `appsettings.Production.json`, and the settings kept in the database |
| [Updates](updates.md) | Self-update with rollback, GitHub releases, version rules |
| [Operations](operations.md) | Service control, logs, backups, the command line |

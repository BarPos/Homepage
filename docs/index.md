---
title: Home
layout: home
nav_order: 1
---

# Homepage

Homepage is self-hosted website software: pages, a blog and a web **admin** in one program. Pages and posts are
built in a **drag-and-drop editor** from blocks, a **theme** decides how every page looks, and **permission groups**
decide who may change what. It runs on one Ubuntu server with PostgreSQL, without Docker.

[Get started](getting-started.md){: .btn .btn-primary .mr-2 }
[Architecture](architecture.md){: .btn }

## Features

- **Visual editor:**
  - Pages and posts are trees of blocks: sections, columns, headings, text, images, buttons, quotes, post lists, menus.
  - Drag blocks from the palette into the layers tree, or drag them around in the page itself. The canvas is the real
    page, rendered by the server, at desktop, tablet or phone width.
  - **Markdown is a block.** A post starts as one Markdown block; images, quotes or buttons go in between paragraphs.
  - Drafts are saved as you work; **Publish** makes them public and keeps a revision.
- **Themes:**
  - Colors for light and dark mode, fonts, sizes, corner radius, spacing, shadows, **frosted glass** surfaces and a
    soft color backdrop. A live preview shows every change before it is saved.
  - The header and footer of each theme are edited in the visual editor.
  - Three themes ship with the release; duplicate one to make it your own.
- **Permission groups**, modelled on OTRS Community Edition:
  - Every page, post and file belongs to a group. Users get `ro`, `create`, `edit`, `publish`, `delete` or `rw` on
    a group, directly or through roles.
  - System groups decide who may change themes, layout, raw HTML, users and settings.
- **Public site:** blog with search, RSS, sitemap, canonical links, output caching, and no JavaScript framework.
- **Operations:**
  - One-command backup and restore of the database, configuration, media and program.
  - Can share the machine's PostgreSQL with other applications: install, backups, restores and uninstall touch only
    Homepage's own role and database.
  - Serves HTTPS itself, or plain HTTP on 127.0.0.1 behind Caddy or nginx (the usual choice for a public site).
- **Updates:** an admin button updates the server from the GitHub releases, with a database backup first and an
  automatic rollback when the new version does not start.
- **No constant connection:** every click in the admin and the editor is an ordinary HTTP request. There is no
  WebSocket, no SignalR and no per-user state held on the server.

## Components

| Component | Runs on | Role |
|---|---|---|
| **Server** | Ubuntu 24.04 LTS or newer (systemd service) | Public site, admin, visual editor, media, background jobs, PostgreSQL data |
| **Reverse proxy** (optional) | The same machine | Caddy or nginx with Let's Encrypt in front of the server in `http` mode |
| **Browser** | Visitors and editors | Server-rendered pages; the admin uses htmx, Alpine.js and SortableJS, bundled with the server |

```
Visitor ──HTTPS──► Caddy / nginx ──HTTP 127.0.0.1:5080──► Homepage server ── PostgreSQL
Editor  ──HTTPS──► (same)                      /admin ──┘        │
                                                                 └── data/: media, keys, staged updates
```

## Documentation map

| | |
|---|---|
| [Getting started](getting-started.md) | From an empty Ubuntu machine to the first published page |
| [Server](server/index.md) | Installation, configuration reference, updates, operations |
| [Admin](admin/index.md) | Admin pages, the visual editor, blocks, themes, media and menus |
| [Permissions](permissions.md) | Users, groups, roles and permission types |
| [Security](security.md) | Trust model and hardening |
| [Reference](reference/index.md) | Content format, URLs, version rules |
| [Development](development.md) | Building, testing, releases, these docs |
| [Troubleshooting](troubleshooting.md) | Common problems |

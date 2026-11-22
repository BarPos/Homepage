---
title: Getting started
nav_order: 2
---

# Getting started
{: .no_toc }

This page takes you from an empty Ubuntu machine to the first published page.

1. TOC
{:toc}

## What you need

- **Server:** Ubuntu 24.04 LTS or newer (x64 or arm64), with root access.
  - PostgreSQL 16+ can be installed by the installer, or you can point it at an existing server.
  - A PostgreSQL already on this machine can keep serving other applications: the installer only adds its own role
    and database to it (see [Sharing PostgreSQL](server/installation.md#sharing-postgresql-with-other-applications)).
  - No Docker and no .NET runtime are needed; the server is self-contained.
- **A DNS name** that points at the server, for example `www.example.com`.
- **Network:** visitors reach the server on 80/tcp and 443/tcp.
- **Recommended:** [Caddy](https://caddyserver.com/) (or nginx) on the same machine, for HTTPS certificates from
  Let's Encrypt that renew by themselves.

## 1. Install the server

Download the installer from the [latest release](https://github.com/BarPos/Homepage/releases/latest) and run it as
root. It downloads the matching server package for your CPU from the same release:

```sh
curl -fsSLO https://github.com/BarPos/Homepage/releases/latest/download/install.sh
sudo bash install.sh
```

Offline: copy `Homepage.Server-<version>-linux-x64.tar.gz` (or `-linux-arm64`) to the server and run
`sudo bash install.sh --package Homepage.Server-<version>-linux-x64.tar.gz`.

The installer asks for:
- the database: PostgreSQL on this machine (installed when missing; one that is already there is shared, and gets only a
  new `homepage` role and database), or a PostgreSQL server elsewhere;
- how the site is served:
  - **http** on 127.0.0.1:5080, behind Caddy or nginx on this machine (choose this for a public website);
  - **https** on port 443 with an existing PEM certificate, or a generated self-signed one;
- the first admin account.

Details and unattended installs: [Server installation](server/installation.md).

## 2. Put Caddy in front (http mode)

At the end, the installer prints a Caddy site block for your server name. Install Caddy and put the block in
`/etc/caddy/Caddyfile`:

```
www.example.com {
    @health path /healthz /healthz/*
    respond @health 404
    reverse_proxy 127.0.0.1:5080
}
```

`sudo systemctl reload caddy`. Caddy gets a certificate from Let's Encrypt and renews it. In `http` mode on loopback
the installer already trusts the proxy's `X-Forwarded-*` headers (`Site:ForwardedHeaders`), so sign-in limits and the
audit log see the visitor's address and cookies are marked secure. For a proxy on another machine, see
[Configuration](server/configuration.md#site).

## 3. Sign in

Open `https://www.example.com/admin` and sign in with the account you created. The **Dashboard** shows the pages and
posts you can see and a form to create new ones.

Go to **Settings** first:
- set the **site title** and **tagline** (shown in the header), and optionally a **logo** and **favicon** uploaded in
  **Media**;
- set the **public address** (`https://www.example.com`), used in RSS, the sitemap and canonical links;
- choose the **home page**: a page, or a blog's list of posts.

## 4. Edit the home page

A fresh site has a sample home page and a sample post. **Pages → Home → Edit** opens the visual editor:

1. Click a block in the page to select it; its settings appear on the right and save as you type.
2. Drag a block from the palette on the left onto the page (a blue line shows where it goes), or into the **Layers**
   tree. Clicking a block in the palette adds it after the selected one.
3. Drag blocks in the page or in the layers tree to move them.
4. Press **Publish**.

The public page updates at once. More in [Visual editor](admin/editor.md).

## 5. Pick a theme

**Themes** lists the built-in themes. **Activate** switches the whole site. To change colors, fonts or the glass
effect, **Duplicate** a theme, then **Edit design**; the header and footer of your copy open in the visual editor.
See [Themes](admin/themes.md).

## 6. Add people

**Users & permissions → Users** creates accounts. A new user can do nothing until you give them a **role** (for
example *Author* or *Editor*) under **Users ↔ Roles**, or group permissions under **Users ↔ Groups**. See
[Permissions](permissions.md).

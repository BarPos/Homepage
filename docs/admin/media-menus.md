---
title: Media and menus
parent: Admin
nav_order: 4
---

# Media and menus
{: .no_toc }

1. TOC
{:toc}

## Media

**Media → Upload**: one or more files, and the group they belong to (a group where you have `create`).

- **Accepted:** PNG, JPEG, GIF, WebP, AVIF images and PDF files, up to 20 MiB each.
- The type is checked from the **file's content**, not its name: a script renamed to `.png` is refused. SVG is not
  accepted, because it can contain scripts.
- Files are stored under random names in `data/media/` and served at `/media/<name>` with a one-year cache header,
  their real content type, `X-Content-Type-Options: nosniff` and a sandboxing Content-Security-Policy.
- You see the files of groups where you have `ro`. You can delete your own uploads with `create`, others' with
  `delete`.
- To use an image, select an **Image** block in the editor and pick it in the inspector.

{: .note }
Images are served as uploaded; there is no automatic resizing yet. Upload images at the size you need.

{: .warning }
Deleting a file does not check where it is used. Image blocks that show it become empty.

## Menus

**Menus** (needs `layout: rw`) has two menus:

| Menu | Used by |
|---|---|
| `main` | The *Site header* block of the default header |
| `footer` | The *Menu* block in the default footer |

Each link is a label and an address: `/path`, `#anchor`, `https://…`, `http://…`, `mailto:` or `tel:`. Anything else
is refused. **Add link**, **✕** to remove, **Save menu**. The public site updates at once. A *Menu* block can show
either menu anywhere on a page.

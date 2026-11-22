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

- **Accepted:** PNG, JPEG, GIF, WebP, AVIF and ICO images and PDF files, up to 20 MiB each.
- The type is checked from the **file's content**, not its name: a script renamed to `.png` is refused. SVG is not
  accepted, because it can contain scripts.
- Files are stored under random names in `data/media/` and served at `/media/<name>` with a one-year cache header,
  their real content type, `X-Content-Type-Options: nosniff` and a sandboxing Content-Security-Policy.
- You see the files of groups where you have `ro`. You can delete your own uploads with `create`, others' with
  `delete`.
- To use an image, select an **Image** block in the editor and pick it in the inspector.
- The list can be searched by file name and filtered by group and kind (images, PDFs), with paging.

### Logo and favicon

**Settings → Logo** and **Settings → Favicon** pick images from Media.
- The **logo** is shown by the *Logo / site name* block in the header (height set in the block). If the file is
  deleted later, the site title is shown instead.
- The **favicon** should be a square PNG of at least 180 × 180 pixels, or an ICO file. SVG is not accepted (see
  above).

{: .note }
Images are served as uploaded; there is no automatic resizing yet. Upload images at the size you need.

{: .warning }
Deleting a file does not check where it is used. Image blocks that show it become empty.

## Menus

**Menus** (needs `layout: rw`). A fresh site has two:

| Menu | Used by |
|---|---|
| `main` | The *Menu* block in the default header's site bar |
| `footer` | The *Menu* block in the default footer |

- **New menu:** a name and an optional key (made from the name when empty). A *Menu* block can then show it anywhere:
  a page, the header or the footer.
- **Rename** a menu in its name field; the key stays, so blocks keep pointing at it.
- **Delete menu** removes it. *Menu* blocks that showed it render nothing until you pick another menu.
- Each link is a label and an address: `/path`, `#anchor`, `https://…`, `http://…`, `mailto:` or `tel:`. Anything
  else is refused. **Add link**, **✕** to remove, **Save menu**. The public site updates at once.

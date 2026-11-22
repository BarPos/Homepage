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

**Media → Upload files** opens a dialog: one or more files, and the group they belong to (a group where you have
`create`; left out when there is only one).

- **Accepted:** PNG, JPEG, GIF, WebP, AVIF, ICO and SVG images and PDF files, up to 20 MiB each (SVG up to 1 MiB).
- The type is checked from the **file's content**, not its name: a script renamed to `.png` is refused.
- **SVG** files are checked before they are stored: well-formed SVG only, and no scripts, event handlers, embedded
  HTML, frames, links to other files or styles that load other files. Like every media file, they are served with a
  sandboxing Content-Security-Policy, so a script would not run even when opened directly.
- Files are stored under random names in `data/media/` and served at `/media/<name>` with a one-year cache header,
  their real content type, `X-Content-Type-Options: nosniff` and a sandboxing Content-Security-Policy.
- You see the files of groups where you have `ro`. You can delete your own uploads with `create` as long as they are
  not used on a published page, in a theme or as the logo or favicon; others', and files in use, need `delete`.
- Image blocks can only show files from groups where you have `ro`.
- To use an image, select an **Image** block in the editor and pick it in the inspector.
- The list can be searched by file name and filtered by group and kind (images, PDFs), with paging.

### Logo and favicon

**Settings → Logo** and **Settings → Favicon** pick images from Media (the pickers can be searched and show
thumbnails).
- The **logo** is shown by the *Logo / site name* block in the header (height set in the block). If the file is
  deleted later, the site title is shown instead.
- **Favicon:** give one, two or all three formats; every page lists them and each browser takes the best one it
  supports.

  | Format | |
  |---|---|
  | SVG | Sharp at every size; modern browsers prefer it. A `@media (prefers-color-scheme: dark)` rule inside the file can give it a dark-mode color. |
  | PNG | Square, at least 180 × 180. Also the icon when the site is added to an iPhone or iPad home screen. |
  | ICO | For older browsers and tools. `/favicon.ico` redirects to it (or to the PNG or SVG when there is no ICO). |

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

The **Menus** page lists the menus with their links. **Edit** opens a menu in the menu editor; **New menu** opens an
empty one.

- **Name** and **key**. The key is made from the name when empty, cannot be `new`, and cannot be changed later; a
  *Menu* block refers to the menu by its key, anywhere: a page, the header or the footer.
- **Links:** each is a label and an address: `/path`, `#anchor`, `https://…`, `http://…`, `mailto:` or `tel:`.
  **Choose…** next to the address picks a page, blog or post of the site (searchable): the link then follows it when
  it is renamed or moved, and is left out while it is unpublished or after it is deleted. For another site, type the
  address. Anything else is refused, and the editor keeps what you typed.
- A link can have an **icon** (the square before the label opens the icon chooser; **⌫** removes it) and can **open in
  a new tab** (the checkbox).
- **+ Add link**, **↑** / **↓** to reorder, **✕** to remove, then **Create menu** or **Save menu**. The public site
  updates at once.
- **Delete menu** (in the editor) removes it. *Menu* blocks that showed it render nothing until you pick another
  menu.

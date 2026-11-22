---
title: Blocks
parent: Admin
nav_order: 2
---

# Blocks
{: .no_toc }

1. TOC
{:toc}

Every block's settings are chosen from fixed values (sizes, styles, theme colors), never free-form CSS, so a page
always follows the active theme. Text is HTML-encoded; Markdown and the HTML block are sanitized.

## Layout

### Section
A full-width band with the page container inside. Top level only.

| Setting | Values | Default |
|---|---|---|
| Style | *Default*, *Hero* (large heading, short muted text), *Narrow* (760 px content), *Band* (lines above and below), *Glass* (content on a frosted panel) | Default |
| Background | *Page background* (transparent over the theme's backdrop), *Surface* (translucent), *Alternate surface*, *Primary*, *Accent* | Page background |
| Padding | None, Sm, Md, Lg, Xl | Lg |

Contains any content block, *Columns* and *Site header*.

### Columns
Side-by-side columns that stack on screens narrower than 760 px.

| Setting | Values | Default |
|---|---|---|
| Column widths | *Equal* (any number of columns), *One two*, *Two one*, *One three*, *Three one* (two columns) | Equal |
| Gap | None, Sm, Md, Lg, Xl | Md |

Contains only *Column* blocks; it is created with two.

### Column
One column of a *Columns* block. Contains content blocks. No settings.

### Spacer
Empty vertical space: None, Sm, Md, Lg, Xl.

### Divider
A thin horizontal line that fades out at both ends. No settings.

## Content

### Heading

| Setting | Values | Default |
|---|---|---|
| Text | up to 300 characters, required | Heading |
| Level | 1 to 4 (use one level 1 per page) | 2 |
| Alignment | Left, Center, Right | Left |

### Text (Markdown)
Markdown with tables, footnotes, task lists, auto-links and heading anchors. Raw HTML in it is sanitized: no
scripts, event handlers or `javascript:` links. Up to 200,000 characters.

```markdown
**bold**, *italic*, [a link](https://example.com), `code`
# Heading
- list item
> quote
```

### Image
| Setting | Values | Default |
|---|---|---|
| Image | an image from **Media** that you can see | none |
| Alternative text | for screen readers and search engines | empty |
| Caption | shown under the image | empty |
| Width | *Normal* (up to 760 px), *Wide* (container), *Full* (fills the container) | Normal |

### Button
| Setting | Values | Default |
|---|---|---|
| Text | up to 100 characters, required | Read more |
| Link | `/path`, `#anchor`, `https://…`, `http://…`, `mailto:`, `tel:`. Anything else is refused. | `/` |
| Style | *Primary*, *Secondary* (accent color), *Outline* (glass) | Primary |
| Alignment | Left, Center, Right | Left |

### Quote
Quote text (up to 2,000 characters) and an optional attribution.

### HTML
Raw HTML, for embeds the other blocks cannot do. **Needs `html: rw`** to add, change, move or delete it; others see it
in the editor but cannot touch it. The HTML is still sanitized: scripts, event handlers and `javascript:` URLs are
removed, and the site's Content-Security-Policy blocks inline scripts.

## Dynamic

### Latest posts
The newest published posts, read when the page is rendered.

| Setting | Values | Default |
|---|---|---|
| Number of posts | 1 to 50 | 5 |
| Layout | *List*, *Grid* | List |
| Show excerpts | yes / no | yes |

## Site

### Menu
A menu from **Menus** (`main` or `footer`), aligned left, center or right.

### Site header
The site title, the tagline (optional) and a menu, on a floating glass bar. Only in a theme's **header**.

## Nesting rules

| Block | May be placed | May contain |
|---|---|---|
| Section | top level only | content blocks, Columns, Site header |
| Columns | top level, in a section | Column only |
| Column | in Columns only | content blocks |
| Content blocks (Heading, Text, Image, Button, Quote, Spacer, Divider, HTML, Latest posts, Menu) | top level, in a section, in a column | nothing |
| Site header | in a theme header (top level or section) | nothing |

Further limits: at most 1,000 blocks per page and 6 levels of nesting. A content block at the top level of a page
gets the page container automatically, so it does not run edge to edge.

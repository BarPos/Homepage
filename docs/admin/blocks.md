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

Contains any content block, *Columns*, and in a theme's header or footer the *Site bar* and *Logo / site name*.

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
| Blog | *All blogs*, or one blog | All blogs |
| Number of posts | 1 to 50 | 5 |
| Layout | *List*, *Grid* | List |
| Show excerpts | yes / no | yes |

## Site

### Menu
A menu from **Menus**, aligned left, center or right. If its menu is deleted, the block shows nothing until you pick
another one.

### Site bar
The bar at the top (or bottom) of the site: a row holding the brand, menus, buttons, spacers or text. Only in a
theme's header or footer.

| Setting | Values | Default |
|---|---|---|
| Stay on top while scrolling | yes / no | yes |
| Style | *Glass* (floating frosted bar), *Solid* (full-width surface with a border), *Transparent* | Glass |
| Arrange items | *Spread* (first left, last right), *Start*, *Center*, *End* | Spread |

Contains *Logo / site name*, *Menu*, *Button*, *Spacer*, *Text* and *HTML*.

### Logo / site name
Links to the home page. Only in a theme's header or footer.

| Setting | Values | Default |
|---|---|---|
| Show | *Logo and text*, *Logo only*, *Text only* | Logo and text |
| Text | empty = the site title from **Settings** | empty |
| Show tagline | yes / no | yes |
| Logo height | 16 to 120 px | 32 |

The logo is the one chosen in **Settings → Logo**. Without a logo, or when its file has been deleted, the block shows
the text instead, whatever *Show* says.

## Nesting rules

| Block | May be placed | May contain |
|---|---|---|
| Section | top level only | content blocks, Columns; in a header or footer also Site bar and Logo / site name |
| Columns | top level, in a section | Column only |
| Column | in Columns only | content blocks |
| Content blocks (Heading, Text, Image, Button, Quote, Spacer, Divider, HTML, Latest posts, Menu) | top level, in a section, in a column; Button, Menu, Spacer, Text and HTML also in a site bar | nothing |
| Site bar | header or footer: top level or in a section | Logo / site name, Menu, Button, Spacer, Text, HTML |
| Logo / site name | header or footer: top level, in a section or a site bar | nothing |

Further limits: at most 1,000 blocks per page and 6 levels of nesting. A content block at the top level of a page
gets the page container automatically, so it does not run edge to edge.

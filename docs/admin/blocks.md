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
| Content width | Narrow (760 px), Normal (the theme's width), Wide, Full (edge to edge) | Normal |
| Text alignment | Left, Center, Right, Justify | Left |
| Minimum height | Auto, a third, half, or the full height of the screen (for a hero) | Auto |
| Content position | Top, Center, Bottom or Stretch, when there is a minimum height | Center |
| Background image | an image from **Media**, covering the section | none |
| Darken the image | 0 to 90 %, so white text stays readable (text in a section with an image is white) | 40 |

Contains any block except another section, and in a theme's header or footer also the *Site bar*.

### Columns
Side-by-side columns. Can go anywhere, also in a column (columns in columns), a group or a site bar.

| Setting | Values | Default |
|---|---|---|
| Column widths | *Equal* (any number of columns), *One two*, *Two one*, *One three*, *Three one* (two columns) | Equal |
| Gap | None, Sm, Md, Lg, Xl | Md |
| Vertical alignment | Top, Center, Bottom, Stretch (all columns as tall as the tallest) | Stretch |
| Stack on small screens | below 760 px the columns go under each other | on |
| Reverse the order when stacked | the last column comes first on phones | off |

Contains only *Column* blocks; it is created with two.

### Column
One column of a *Columns* block. Contains any block except a section (also columns and groups).

| Setting | Values | Default |
|---|---|---|
| Text alignment | Left, Center, Right, Justify | Left |
| Vertical position | Top, Center, Bottom, Stretch, within the row | Top |
| Background | none, or a theme color | none |
| Padding | None to Xl | None |

### Group (row or stack)
Arranges blocks in a **row** or a **vertical stack**: a logo and a menu side by side, buttons in a row, a column of
links in a footer. Can go anywhere, also inside site bars, columns and other groups.

| Setting | Values | Default |
|---|---|---|
| Direction | Horizontal (a row) or Vertical (a stack) | Horizontal |
| Distribute | Start, Center, End, Space between | Start |
| Align items | Start, Center, End, Stretch (across the direction) | Center |
| Gap | None to Xl | Sm |
| Wrap onto more lines | when a row is too wide | on |
| Stack vertically on small screens | a row becomes a stack below 760 px | off |
| Background, Padding | as for a column | none |

### Spacer
Empty space: None, Sm, Md, Lg, Xl. In a row (a horizontal group or a site bar) it is horizontal, and **Fill the free
space** makes it take all the room left, pushing the blocks after it to the end.

### Divider
A thin line: *Line* (fading out at both ends), *Dashed*, *Dotted* or *Space* (invisible), with the space around it
(None to Xl). In a row (a horizontal group or a site bar) it is a vertical line between items.

## Content

### Heading

| Setting | Values | Default |
|---|---|---|
| Text | up to 300 characters, required | Heading |
| Level | 1 to 4 (use one level 1 per page) | 2 |
| Alignment | Left, Center, Right, Justify | Left |
| Icon | a Font Awesome icon before the text | none |
| Link | makes the heading a link (**Choose…** for a page, blog or post) | none |

### Text (Markdown)
Markdown with tables, footnotes, task lists, auto-links and heading anchors. Raw HTML in it is sanitized: no
scripts, event handlers or `javascript:` links. Up to 200,000 characters.

**Alignment** (left, center, right, justify) is a setting of the whole block: the alignment buttons in the editor's
toolbar set it, and the editor shows the text aligned. (Markdown has no alignment of its own.)

The inspector has an editor with two modes, switched with the tabs above it: **Rich text** (format with the toolbar,
as in a word processor) and **Markdown** (the text itself, with a preview tab). Both edit the same Markdown; your
last choice is remembered in the browser. **⤢** opens a bigger editor (Esc closes it).

The toolbar above the text: paragraph or heading 1–4, bold, italic, strikethrough, code, link (**Ctrl+K**), lists,
checklist, quote, code block, table, divider, alignment, undo and redo. Buttons show the formatting at the cursor. The
link dialog takes link text and any address (with *open in a new tab*), or a page, blog or post of the site.

Rich text cannot show everything Markdown can. Footnotes, HTML and `{…}` link attributes appear there as small grey
chips (hover one to see it) and are kept exactly as written; switch to Markdown to change them. Text between HTML
tags, such as the inside of a `<details>` block, stays editable. Rich text may write tables and lists in a different
but equivalent style (`| --- |`, `*` bullets).

A link to a page, post or blog chosen in the dialog points at it itself (`[text](page:12)`) and keeps working when it
is renamed or moved. If it is deleted or unpublished, the words stay and the link goes.

```markdown
**bold**, *italic*, [a link](https://example.com), `code`
[opens in a new tab](https://example.com){new}
# Heading
- list item
> quote
```

`{new}` after a link (or `{target=_blank}`) opens it in a new tab, with `rel="noopener noreferrer"`. Other targets
are removed.

### Image
| Setting | Values | Default |
|---|---|---|
| Image | an image from **Media** that you can see | none |
| Alternative text | for screen readers and search engines | empty |
| Caption | shown under the image | empty |
| Width | *Normal* (up to 760 px), *Wide* (container), *Full* (fills the container) | Normal |
| Maximum width | in pixels, 0 = no limit (a small logo or badge) | 0 |
| Alignment | Left, Center, Right | Left |
| Shape | *Theme* (the theme's corners), *Square*, *Rounded*, *Circle* (cropped to a circle) | Theme |
| Link | makes the image a link (**Choose…** for a page, blog or post), optionally in a new tab | none |

### Button
| Setting | Values | Default |
|---|---|---|
| Text | up to 100 characters, required | Read more |
| Link | **Choose…** picks a page, blog or post of the site: the button then links to it wherever it moves (it is stored as `page:12`, `post:5` or `blog:3`, and the inspector shows where it points). Or type `/path`, `#anchor`, `https://…`, `http://…`, `mailto:`, `tel:`; anything else is refused. | `/` |
| Style | *Primary*, *Secondary* (accent color), *Outline* (glass) | Primary |
| Alignment | Left, Center, Right | Left |
| Open in a new tab | adds `target="_blank"` and `rel="noopener noreferrer"`, and tells screen readers | off |
| Icon | a [Font Awesome Free](https://fontawesome.com) icon (2,800+, solid, regular and brands), chosen and searched in a dialog | none |
| Icon image | an image from **Media** instead of an icon (for a logo, say) | none |
| Icon position | before or after the text | before |
| Size | Small, Medium, Large | Medium |
| Full width | the button fills its column | off |

If the chosen page is deleted or unpublished, the button is left out of the public site, and the editor says so.

Icons are drawn as inline SVG in the text color: no web font or extra stylesheet is loaded on the page. Font Awesome
Free icons are licensed CC BY 4.0.

### Quote
Quote text (up to 2,000 characters), an optional attribution, and an alignment (left, center, right, justify).

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
| Show dates, Show authors | yes / no | yes |
| Columns (grid) | 1 to 4 posts side by side in the grid layout (one on phones) | 2 |
| Show excerpts | yes / no | yes |

## Site

### Menu
A menu from **Menus**. If its menu is deleted, the block shows nothing until you pick another one. Links can have an
icon and open in a new tab (set in the menu editor).

| Setting | Values | Default |
|---|---|---|
| Alignment | Left, Center, Right | Left |
| Direction | Horizontal (a row) or Vertical (one link per line, e.g. in a footer column) | Horizontal |
| Look | *Links*, *Pills* (a soft background on hover), *Underline*, *Buttons* (outlined) | Links |
| Gap | None to Xl | Sm |
| Fold into a ☰ button on small screens | below 760 px the links are behind a menu button (no script needed) | off |

Links to a page that is not published are left out on the site; in the editor they show with a dashed outline.

### Site bar
The bar at the top (or bottom) of the site: a row holding the brand, menus, buttons and any other blocks except
sections (also columns and groups). Only in a theme's header or footer.

| Setting | Values | Default |
|---|---|---|
| Stay on top while scrolling | yes / no | yes |
| Style | *Glass* (floating frosted bar), *Solid* (full-width surface with a border), *Transparent* | Glass |
| Arrange items | *Spread* (first left, last right), *Start*, *Center*, *End* | Spread |
| Width | Narrow, Normal, Wide, Full | Normal |
| Height | Compact, Normal, Roomy | Normal |
| Gap | None to Xl, between items | Sm |
| Stack vertically on small screens | the items go under each other below 760 px | off |

Headings and images inside a site bar are sized for it (a small heading, an image up to 44 px high).

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
| Section | top level only | every block except sections; in a header or footer also the Site bar |
| Columns | anywhere | Column only |
| Column | in Columns only | every block except sections and site bars |
| Group | anywhere | every block except sections and site bars |
| Content blocks (Heading, Text, Image, Button, Quote, Spacer, Divider, HTML, Latest posts, Menu) | anywhere | nothing |
| Site bar | header or footer: top level or in a section | every block except sections and site bars |
| Logo / site name | header or footer: anywhere | nothing |

Further limits: at most 1,000 blocks per page and 10 levels of nesting. A content block at the top level of a page
gets the page container automatically, so it does not run edge to edge.

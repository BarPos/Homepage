---
title: Themes
parent: Admin
nav_order: 3
---

# Themes
{: .no_toc }

1. TOC
{:toc}

A theme decides how every page looks: colors, fonts, sizes, corners, spacing, shadows, glass, the page backdrop, and
the site **header** and **footer**. Pages only store choices such as "padding Lg" or "background Primary", so
switching or editing the theme restyles all of them.

## Built-in themes

| Theme | Look |
|---|---|
| **Glass** (default) | Warm off-white and near-black, cobalt and vermilion, frosted glass header and panels over soft color fields, capsule buttons. Follows the visitor's light or dark mode. |
| **Editorial** | Paper tones, serif type, square corners, no glass, no backdrop |
| **Midnight** | Always dark, blue and teal, strong glass over aurora bands |

Built-in themes are **read-only**, and a release may restyle them. **Duplicate** one to make a copy you can change;
copies are never touched by updates.

## Permissions

| Action | Needs |
|---|---|
| See the Themes page and previews | `themes: ro` or `layout: ro` |
| Activate, duplicate, edit design, custom CSS | `themes: rw` |
| Edit the header and footer of a theme | `layout: rw` |

## Design tokens

**Edit design** on a copied theme. The preview on the right changes as you edit; nothing is live until **Save theme**.

| Group | Token | Range |
|---|---|---|
| Colors | page background, surface, alternate surface, text, muted text, borders, primary, text on primary, accent, text on accent | `#rrggbb`, separately for **light** and **dark** |
| | color mode | follow the visitor's system, always light, always dark |
| Typography | body font, heading font | system sans-serif, humanist, geometric, transitional serif, old-style serif, monospace (system font stacks, nothing downloaded) |
| | base size | 12 to 24 px |
| | heading scale | 1.05 to 1.6 (each heading level is this much larger than the next) |
| | line height | 1.1 to 2.2 |
| Shape | corner radius | 0 to 32 px. From 10 px on, buttons become capsules. |
| | spacing | 0.5 to 2 (multiplies every margin and padding) |
| | content width | 600 to 1600 px |
| | shadows | none, soft, strong |
| Glass | frosted glass surfaces | on / off |
| | blur | 0 to 40 px |
| | opacity | 0.3 (clear) to 0.95 (almost solid) |
| | page backdrop | flat color, soft color fields, aurora bands |

The editor shows the **contrast** of text against its background for each color pair and marks pairs below WCAG AA
(4.5 : 1) in red.

Values outside the ranges are clamped on the server, and colors that are not `#rrggbb` fall back to the default.

## Glass and backdrop

With **glass** on, the glass site bar, glass sections, post cards, quotes, outline buttons and the search field are
translucent and blur what is behind them, with a thin bright edge and a soft sheen along the top. Glass needs
something behind it to look like glass, so it works best with a **backdrop**:

- **soft color fields:** large, very soft patches of the primary and accent colors behind the page, with a fine grain;
- **aurora bands:** diagonal, blurred bands of the same colors;
- **flat color:** just the page background. Glass then looks like plain translucent cards.

With glass off, the same elements become solid cards with a border, and the header becomes a plain bar.

## Custom CSS

**Custom CSS** is added after the theme's variables (needs `themes: rw`). For safety:
- `@import` is removed;
- `url(…)` is kept only for site paths (`/media/…`) and `data:image/…`; anything else becomes `none`;
- `<` is removed, so the CSS cannot end the style element.

Classes worth targeting: `.site-bar`, `.site-bar-inner`, `.site-brand`, `.site-logo`, `.site-footer`, `.sec`, `.sec-hero`, `.sec-glass`, `.post-card`, `.btn`,
`.btn-primary`, `.prose`, `.quote`. Every token is a CSS variable, for example `--color-primary`, `--radius`,
`--glass-blur`, `--font-heading`.

## Header and footer

**Header** / **Footer** open the theme's header or footer in the [visual editor](editor.md). The default header is a
*Site bar* with a *Logo / site name* block and the `main` menu; the default footer is a section with the `footer`
menu and a line of text. Changes to the active theme are live immediately. See [Blocks](blocks.md#site-bar).

## How it is served

The tokens become CSS custom properties at `/theme/<id>/<version>.css`. Every save increases the version, so the URL
changes and browsers may cache each version for a year. The site stylesheet uses only those variables.

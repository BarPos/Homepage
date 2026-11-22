---
title: Visual editor
parent: Admin
nav_order: 1
---

# Visual editor
{: .no_toc }

1. TOC
{:toc}

**Edit** on a page or post, or **Header** / **Footer** on a theme, opens the editor.

```
┌ Back · title · ● saved ─────────── Desktop | Tablet | Phone ── Settings · Publish ┐
│ Blocks (palette)  │                                            │ Inspector       │
│ ▭ Section ▥ Columns│        the real page (canvas)              │ settings of the │
│ H Heading ¶ Text … │                                            │ selected block  │
│ Layers (tree)     │                                            │                 │
└───────────────────┴────────────────────────────────────────────┴─────────────────┘
```

## Selecting and editing

- **Click a block in the canvas** or a row in **Layers** to select it. The canvas outlines it with its name; the
  inspector on the right shows its settings.
- Changes in the inspector **save by themselves** half a second after you stop typing, and the canvas updates. The dot
  next to the title is green when everything is saved, yellow while saving and red after an error (the message
  appears in the corner).
- Inspector buttons: **↑** / **↓** move the block among its neighbours, **⧉** duplicates it with everything inside,
  **✕** deletes it. The **Delete** key deletes the selected block too (not while typing in a field).

{: .note }
There is no undo yet. Deleting a block cannot be reversed in the editor; the last published version stays unchanged
until you publish again.

## Adding blocks

- **Click** a block in the palette: it goes **inside** the selected block if that is a container (section, column),
  otherwise **after** it. With nothing selected it goes at the end.
- **Drag** a block from the palette into the **Layers** tree, to exactly the place you want.
- **Columns** come with two columns; duplicate a column for a third.

The palette shows only blocks you may use: the **HTML** block needs `html: rw`, and **Site header** exists only in a
theme's header.

## Moving blocks

- **Drag a block in the canvas** to another position in the same list or into another container.
- **Drag a row in Layers** to reorder or nest. The tree is easier for nesting and for small blocks.

The server refuses moves that break the [nesting rules](blocks.md#nesting-rules) (for example a section inside a
section) and the editor shows the reason.

## Previewing

- **Desktop / Tablet / Phone** resize the canvas to 100 %, 768 px or 390 px, so the layout responds exactly as on
  those screens.
- The canvas is the real page with the active theme. Links in it do not navigate.
- Header and footer are shown dimmed while you edit a page; while you edit a header, the page content is dimmed.

## Publishing

**Publish** (needs `publish`) makes the draft public and records a revision. The public site updates at once. Without
`publish`, save your work and ask someone who has it; the item shows *Draft* or *changes* in the lists.

## Two people at once

Each change is saved separately. If two people change the same page at the same moment, the second change is applied
on top of the first. When that is not possible, the editor says so and reloads the page with the other person's
version.

## Header and footer

A theme's header and footer are block trees too. **Themes → Header** / **Footer** opens them in the same editor
(needs `layout: rw`). Built-in themes are read-only: duplicate one first. Changes to the active theme's header or
footer are live at once, there is no separate publish step.

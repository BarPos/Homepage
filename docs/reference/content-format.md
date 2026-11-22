---
title: Content format
parent: Reference
nav_order: 1
---

# Content format
{: .no_toc }

1. TOC
{:toc}

## Block trees

Pages, posts, revisions and theme headers and footers are stored as one JSON document each (PostgreSQL `jsonb`):

```json
{
  "schemaVersion": 2,
  "blocks": [
    { "id": "b1f3a9c2d4e", "type": "section",
      "props": { "variant": 1, "background": "bg", "padding": 4 },
      "children": [
        { "id": "b77c0e1a2b3", "type": "heading", "props": { "text": "Hello", "level": 1, "align": 0 }, "children": [] },
        { "id": "b0d9e8f7a6c", "type": "markdown", "props": { "markdown": "Some **text**." }, "children": [] }
      ] }
  ]
}
```

| Field | |
|---|---|
| `schemaVersion` | Version of the format. Content migrations bring older documents up to date. |
| `blocks` | The top-level blocks, in order |
| `id` | Unique within the document: 1 to 40 characters, letters, digits, `-`, `_` (new blocks: `b` + 10 hex digits) |
| `type` | The block type (see [Blocks](../admin/blocks.md)) |
| `props` | The block's settings, camelCase. Choices (variant, padding, alignment, …) are stored as numbers in the order the editor lists them; text as strings; the background as a theme color name. |
| `children` | Child blocks (containers only) |

Each block type has a props class in `Homepage.Core/Blocks/BlockProps.cs` and a Razor component in
`Homepage.Web/Blocks/`. Validation (`BlockValidator`) checks ids, nesting, the tree kind (page, post, header, footer),
the props (ranges, lengths, safe links, known colors), at most 1,000 blocks and 6 levels.

## Unknown blocks

A block whose `type` this version does not know (for example after restoring content from a newer release) is:
- **kept** when the page is loaded and saved;
- shown in the editor as *unsupported*, where it cannot be edited, moved or duplicated (it can be deleted);
- **left out** of the public page.

## Content migrations

When a block's props change shape between releases, the release adds a content migration from schema version *N* to
*N + 1* (`IContentMigration` in `Homepage.Core/Blocks/ContentMigrations.cs`) and raises
`BlockTree.CurrentSchemaVersion`.

- `--migrate-only` runs them after the database migrations, over every draft, published tree, revision and theme
  header/footer. `update.sh` runs `--migrate-only` before it switches to the new release, so a failing migration
  rolls the whole update back through the `pg_dump`.
- A document newer than the running release supports is refused, never silently changed.
- Migrations run step by step and are idempotent: a migrated document is not changed again.

| Version | Release | Change |
|---|---|---|
| 1 | 0.1.0 | First format |
| 2 | 0.2.0 | The fixed `siteheader` block became an editable `sitebar` holding a `sitebrand` and a `menu` (`V1SiteHeaderToSiteBar`), wherever it was in the tree. The site bar keeps the block's id. |

## Search text

Publishing extracts plain text from the published tree (headings, Markdown as plain text, quotes, button labels,
image captions) into `SearchText`. PostgreSQL keeps a generated `tsvector` over title and text (configuration
`simple`), searched with `websearch_to_tsquery`: quoted phrases, `or` and `-word` work in the search box.

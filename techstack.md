# Homepage – Tech Stack

**Homepage** is self-hosted website software: a small CMS / blogging platform. It has a public site (pages, posts, tags, RSS, search) and a password-protected **admin** for writing content, managing media, menus, users and settings.

## Constraint, clarified

What we're avoiding is **Blazor Server / Interactive Server render mode**, where every visitor keeps an open SignalR (WebSocket) circuit and UI state lives on the server.

That still leaves the server free to **render HTML**. Pages are rendered once per ordinary HTTP request. htmx adds partial updates with more plain, stateless HTTP requests that return HTML fragments. There is no circuit and no per-user server memory, and public pages can be output-cached.

---

## TL;DR – Recommended stack

| Layer | Choice |
|---|---|
| Runtime | **.NET 10 (LTS)**, SDK pinned in `global.json` (`10.0.100`, `rollForward: latestFeature`) |
| Web framework | **ASP.NET Core Minimal APIs** + **static Razor components** (`RazorComponentResult`), no render modes |
| Public site | Server-rendered HTML, **output caching**, almost no JS (htmx only for search, comments, "load more") |
| Admin UI | **htmx + Alpine.js** |
| Website editor | **Drag & drop visual page builder**: iframe canvas, **SortableJS**, htmx inspector, server-rendered blocks (section 3) |
| Content format | Pages: **block tree in `jsonb`**, rendered by Razor components. Posts: the same block tree, where **Markdown is a block type** (Markdig + **HtmlSanitizer**) mixed freely with other blocks |
| Theming | Versioned **themes** (design tokens, block variants, header/footer layout, fonts) with a **live theme editor** |
| Styling | **Tailwind CSS** (standalone CLI, no Node) mapped to **CSS variables**, so themes switch at runtime |
| Database | **PostgreSQL 16+** |
| Data access | **EF Core + Npgsql**, EF migrations (also applied at every start and by `--migrate-only`) |
| Media | Files in `data/media/`, resized variants with **NetVips** or **SkiaSharp** |
| Auth & permissions | **ASP.NET Core Identity** accounts (cookie, 2FA) + **OTRS-style permission groups**: users, groups, roles, `ro`/`create`/`edit`/`publish`/`delete`/`rw` |
| Background jobs | `BackgroundService` + `PeriodicTimer` (scheduled publishing, update checks, cleanup) |
| Dev & testing | **Docker**: Compose for Postgres, **Testcontainers** for tests, Ubuntu containers to test the installer |
| Production | **No Docker.** Self-contained Linux build, `install.sh`, **systemd**, self-update with rollback. This is the **DomainNotice workflow** (section 8) |
| CI / releases | GitHub Actions: manual `build` + `release` workflows, tarballs + `install.sh` + `SHA256SUMS.txt` |

---

## 1. Server – ASP.NET Core

### Rendering model: Minimal APIs + static Razor components (recommended)
- Endpoints return HTML with `RazorComponentResult<T>`. Components are `.razor` files with **no `@rendermode`**, so there is no SignalR and no WASM. You get typed, composable templates and nothing else from Blazor.
- Each endpoint returns the full page or only a fragment, based on the `HX-Request` header.

```csharp
app.MapGet("/admin/posts", async (AppDb db, HttpRequest req, [AsParameters] PostQuery q) =>
{
    var page = await db.Posts.Filter(q).ToPageAsync(q.Page);
    return req.Headers.ContainsKey("HX-Request")
        ? new RazorComponentResult<PostTable>(new { Page = page })   // fragment: swap the table only
        : new RazorComponentResult<PostsPage>(new { Page = page });  // full page with admin layout
});
```

Two rules for this switch:
- **`hx-boost` navigation** sends `HX-Request: true` *and* `HX-Boosted: true`, but expects a full page. Treat `HX-Boosted` as a full-page request (Htmx.Net: `Request.IsHtmx() && !Request.IsHtmxBoosted()`).
- **Output-cached endpoints that branch on these headers must vary by them** (`policy.SetVaryByHeader("HX-Request", "HX-Boosted")`). Otherwise a cached fragment could be served as a full page, or the reverse.

**Alternative:** Razor Pages (`OnGet`/`OnPost` handlers returning `Partial(...)`). It is just as viable and very productive for admin CRUD screens. Pick one model and use it everywhere.

### Helpful libraries
- **Htmx.Net**: helpers for `HX-*` request and response headers (`HX-Trigger`, `HX-Redirect`, `HX-Retarget`).
- **Markdig**: Markdown → HTML with extensions (tables, footnotes, task lists, auto-identifiers for heading anchors).
- **HtmlSanitizer (Ganss.Xss)**: sanitizes rendered HTML if authors may embed raw HTML.
- **Microsoft.Extensions.Hosting.Systemd**: `UseSystemd()` for `Type=notify` readiness and journal logging (as DomainNotice does).

### Built-in ASP.NET Core features to use
- **Output caching** (`AddOutputCache`) for public pages, with **tag-based eviction**:
  - `post:42` when that page or post is published;
  - a **`site`** tag on every page, evicted when the theme, header/footer, a menu, a **global block** or site settings change;
  - a `block:global:<id>` tag on pages that use a given global block, for narrower eviction.

  Editor canvas and preview endpoints are **never cached**.
- **Response compression**, and static files with long-lived `Cache-Control` and fingerprinted asset URLs (`MapStaticAssets`).
- **Rate limiting** (`AddRateLimiter`) on sign-in, comment and search endpoints.
- **Antiforgery** for all admin forms and htmx requests (see section 6).
- **Health endpoint** `/healthz` that answers only from loopback and returns `{"status":"ok","version":"…"}`. The updater uses it (section 8).

---

## 2. Frontend

The platform has two very different surfaces.

### Public site
- Plain semantic HTML rendered on the server, fast first paint, SEO-friendly. No JS framework.
- `hx-boost` (optional) for SPA-like navigation without a SPA.
- Small htmx islands: live search (`hx-get="/search" hx-trigger="input changed delay:300ms"`), "load more" pagination, comment form submission.

### Admin
- **htmx** for lists, filters, inline edits, dialogs and saving, plus **Alpine.js** for local UI state (dropdowns, tabs, unsaved-changes warning, slug auto-generation).
- **Pages and site layout:** the **visual drag-and-drop editor** (section 3).
- **Blog posts:** the same visual editor, starting with a **Markdown block** in a writing-first view; other blocks can be mixed in anywhere (see 3.7). The Markdown block's editor is a **textarea + live preview through htmx**: `hx-post="/admin/preview" hx-trigger="input changed delay:500ms"` renders with the same Markdig pipeline as the public site. **EasyMDE** can add a toolbar.
- **SortableJS** for drag-and-drop in the page editor, and for ordering menus and galleries. On drop, `hx-post` the new order.
- Media upload with drag-and-drop through `hx-encoding="multipart/form-data"` and progress events.

### Alternatives considered

| Option | Persistent connection? | Verdict |
|---|---|---|
| **htmx + Alpine.js** ⭐ | No | Logic stays in C#, no JS build, easy to self-host offline |
| Unpoly | No | Similar to htmx with more built in (modals/layers, validation). A good alternative |
| Hotwire Turbo | No (unless Streams over WS) | Works, but Rails-centric |
| Datastar | **Yes** (SSE) | Close to the always-connected model we're avoiding |
| JSON API + SPA (Svelte/Vue/React) | No | Not for the whole admin: a second codebase and a Node toolchain. At most an **editor-only island** (Option C in 3.3) |
| Blazor WebAssembly | No | Heavy download, poor fit for a public site |

**Vendor** `htmx.min.js`, `alpine.min.js` and the editor scripts into `wwwroot/` (pinned versions, no CDN). The release tarball must be complete, and the site should work without third-party requests.

---

## 3. Visual website editor (drag & drop) & theming

> **Scope assumption:**
> - The visual editor builds **pages**, plus the site-wide **header, footer and layout** (edited as special block trees).
> - **Blog posts are block trees too**, with **Markdown as a block type**. Text and visual blocks mix in the same post (3.7), and pages can use Markdown blocks the same way.

### 3.1 The key decision: content is a structured block tree, not saved HTML
Store each page as **JSON (`jsonb`)**: a tree of typed blocks. Every block type has a **C# model** and a **Razor component** that renders it on the server.

```json
{ "type": "section", "id": "b1", "variant": "hero", "props": { "background": "surface-alt", "padding": "xl" },
  "children": [
    { "type": "columns", "id": "b2", "props": { "ratio": "1:1", "stackOn": "md" }, "children": [
      { "type": "heading", "id": "b3", "props": { "text": "Hello", "level": 1 } },
      { "type": "image",   "id": "b4", "props": { "mediaId": 17, "alt": "…" } } ] } ] }
```

Why this instead of storing the builder's HTML/CSS output:
- **Theming works retroactively.** Blocks reference theme tokens and variants, never hard-coded colors. Switching or editing a theme restyles every existing page.
- **The public site stays server-rendered and cacheable**, with the same Razor components used by the editor preview. There is one renderer, not two.
- **Dynamic blocks are natural:** "latest posts", "tag cloud", "search box" and "contact form" query the DB at render time.
- **The content is safe and validatable.** The server deserializes into typed models (System.Text.Json polymorphism via `[JsonPolymorphic]` / `[JsonDerivedType]`), validates it, and never stores arbitrary markup, except in the HTML block, which needs a permission (section 6).
- **Upgrades can migrate content** (see 3.6).

### 3.2 Block library (built in)
- **Layout:** section, container, columns/grid, spacer, divider.
- **Content:** heading, **Markdown**, rich text (Tiptap), image, gallery, video/embed, button, icon, quote, list.
- **Dynamic:** post list, post grid, tag list, search, navigation menu, breadcrumbs.
- **Forms:** contact form, newsletter. Submissions are stored in the DB and optionally emailed.
- **Reusable:**
  - **global blocks** ("symbols"): edit once, update everywhere;
  - **page templates** and **section presets** (a hero, a pricing table, …) that users drag in as starting points.

Each block type declares:
- a props model with data annotations (`[Display]`, `[Range]`, `[Required]`, custom `[ThemeColor]`, `[MediaPicker]` …). The **inspector form is generated** from it, and the server validates it;
- the allowed children (for example columns accept only column blocks);
- its **style variants** from the theme (3.5).

### 3.3 Editor UI: options

**Option A – Custom editor on the current stack** ⭐ recommended
- **Layout:** an admin screen with a block **palette**, a **layers tree**, the **canvas** and an **inspector**.
- **Canvas:** an **iframe** loading `/admin/editor/canvas/{pageId}`, the real server-rendered page in edit mode, with `data-block-id` attributes. The iframe isolates theme CSS from admin CSS, and **responsive preview** is just resizing the iframe (desktop/tablet/phone), so media queries behave for real.
- **Drag & drop:** **SortableJS** with nested lists (palette → canvas, reorder, move between containers), mirrored in the layers tree. On drop the client sends a small operation such as `{op:"move", id, parent, index}`.
- **Editing:** click to select (Alpine holds the selection). The inspector is an htmx-loaded form. Changes `hx-post` (debounced) to the server, which validates them, updates the draft and returns the **re-rendered block fragment** to swap into the canvas. Inline text editing uses a small contenteditable/Tiptap instance on the selected block only.
- **Undo/redo:** a client-side operation stack. Each op has an inverse, and the inverse is posted like any other change.
- **Autosave** keeps a draft separate from the published version. **Publish** creates a revision and evicts the cache. Concurrent editors are detected through Postgres `xmin` row versions (optimistic concurrency) with a "someone else saved this page" prompt.
- **Effort:** this is the largest single piece of the project. Nested drag-and-drop, selection, undo and inline editing are real work. In return it needs no JS framework or bundler and keeps a single renderer.

**Option B – Embed GrapesJS** (framework-agnostic web builder, BSD license; verify the LICENSE file in its repo)
- Mature drag and drop, layer manager, style manager, device preview and undo, all out of the box.
- It is HTML/CSS-centric, though. To keep 3.1, register **custom GrapesJS component types** that map 1:1 to our block types, disable free-form styling (or limit it to theme tokens), and **save the GrapesJS project JSON converted to our block tree**, not its HTML/CSS export.
- **Effort:** fastest to a working editor. The integration work is in fighting its free-form defaults.

**Option C – A React island for the editor screen only: Puck** (MIT, `@puckeditor/core`), or Craft.js
- Puck is a component-based visual editor whose data model is close to 3.1 (JSON with typed components and fields).
- **Drawbacks:**
  - The editor previews with React components, while the public site renders Razor, so every block exists twice, or the preview fetches server-rendered HTML per block.
  - It adds a Node/Vite build to `build-server.sh` and CI.
  - It goes against the "one codebase" goal, though only on one admin screen.

**Recommendation:** use **Option A**. Its block model, server renderers and the cleanest fit with htmx and theming pay off long term. If time-to-first-editor matters most, prototype with **B** using custom components on the same block JSON. The storage model then stays the same, so switching later is possible.

**No persistent connection:** in every option the editor talks to the server with ordinary HTTP requests (load the draft, post operations, fetch fragments, publish). There is no SignalR and no WebSocket.

### 3.4 Theming
A **theme** is a versioned package:

| Part | What it is |
|---|---|
| **Design tokens** | Colors (light + dark), typography (families, scale, weights), spacing scale, radii, shadows, container widths, as `jsonb` |
| **Block variants** | Named styles per block type ("hero", "card", "muted", "outline" button…) defined as CSS against tokens |
| **Site layout** | Header, footer and default page structure as block trees (editable in the visual editor) |
| **Templates & presets** | Page templates and section presets that look right in this theme |
| **Fonts & assets** | Self-hosted font files (Fontsource-style WOFF2) and images; no Google Fonts CDN, which is better for privacy |
| **Custom CSS** (optional) | Extra CSS; needs `themes: rw` |

**Theme editor** (`/admin/themes/{id}`):
- **Live preview without server round trips:** the token form (color pickers, font selects, sliders) updates **CSS custom properties** on the preview iframe's `:root` via `postMessage` as you drag a slider. Saving `hx-post`s the tokens.
- Show **WCAG contrast checks** for text/background token pairs, and a palette generator (pick a brand color → derive the scale).
- Preview any page, in light and dark mode, at device widths.
- **Built-in themes** ship with releases. Users can **duplicate** one to customize it, and **import/export** a theme as a `.zip` (`theme.json` + fonts + assets), validated on import (schema, file types, sizes).
- Activate, roll back to a previous theme version, and schedule a switch.

### 3.5 Styling implementation (Tailwind + runtime theming)
- **Tailwind CSS standalone CLI** (a single binary, no Node). An MSBuild target or `build-server.sh` runs it before `dotnet publish`. Pin the version and checksum.
- **Tailwind classes cannot be chosen at runtime.** Classes picked in the editor would not exist in the build-time CSS. Therefore:
  - Block components use a **fixed set of classes**, all present at build time, which **reference CSS variables**. Tailwind v4's `@theme` maps utilities to tokens, for example `bg-surface` → `var(--color-surface)`.
  - **User choices are tokens or enumerated variants** (`padding: "xl"`, `background: "surface-alt"`, `variant: "hero"`), rendered as known classes or `data-variant` attributes, never as free-form class strings.
  - Per-block one-off values (an exact image focal point, a custom max width) become **inline CSS variables** on the block (`style="--max-w: 42rem"`), clamped and validated server-side.
- The **active theme's CSS** (tokens + variants + custom CSS) is generated by the server into `data/themes/<id>/<version>.css`. It is served with a **versioned URL** and long cache lifetime, so a theme change is a new URL and needs no cache-busting tricks.
- The **admin UI has its own stylesheet** (DaisyUI is fine there), fully separate from site themes.

### 3.6 Keeping stored content valid across updates
Block JSON lives in the database for years, while block types evolve between releases. So:
- Every block tree carries a **`schemaVersion`**, and each block type can register **content migrations** (C# functions `vN → vN+1`).
- **`--migrate-only` runs the content migrations after the EF migrations.** That is the same step `update.sh` runs before switching releases, so a failed content migration triggers the normal **pg_dump rollback** (8.6).
- A test loads **golden JSON fixtures** from every released schema version and checks that they migrate and render. This is the counterpart of DomainNotice's frozen-protocol tests.
- An unknown block type renders as an "unsupported block" placeholder in the editor and is **omitted on the public site**, never rendered as a crash.
- Theme packages also carry a `schemaVersion` and are migrated the same way.

### 3.7 Blog posts: Markdown and blocks together
There are no editor modes. **Every page and post is a block tree**, and **Markdown is just a block type**. A post can mix Markdown blocks freely with images, galleries, embeds, columns, call-to-action buttons and any other block.

- **The Markdown block:**
  - **Props:** `{ "markdown": "…" }`. It renders with Markdig + HtmlSanitizer inside the block's Razor component. The theme styles it like any other block, through a `prose` typography variant.
  - **Editing:** the inspector, or inline on the canvas, shows a **Markdown textarea** (EasyMDE optional) with a **live preview of that block only** (a debounced `hx-post` that returns the re-rendered block fragment).
  - **Rich-text block** (Tiptap) is offered alongside it for people who prefer WYSIWYG. Both are "text blocks" and can be **converted into each other**.
- **Writing-first experience for posts:**
  - A new post starts with **one Markdown block**, and the editor opens in a distraction-free **writing view**: the Markdown block is full width, with the canvas preview beside it.
  - So writing a plain text post feels like a Markdown editor, while dragging in a block is always one step away.
- **Mixing blocks with text:**
  - **Insert at cursor:** a `+` / `/` command menu inside a Markdown block inserts another block there. It **splits the Markdown block** in two around the new block (text before → block A, new block, text after → block B).
  - **Merge** two adjacent Markdown blocks back into one.
  - Drag and drop works between and around Markdown blocks like any other blocks.
  - **Paste a long Markdown document** and it stays one block. Optionally, an "explode by headings" action splits it into one block per section.
- **Post-specific palette and layout:**
  - Posts get a trimmed block palette (no full-page header/footer sections) and post templates from the theme.
  - The theme's **post layout** (`site_layouts`, `kind = post`) has a **"post content" slot** for the post's block tree. Title, meta, author box, tags and related posts stay theme-controlled.
- **One pipeline for everything:**
  - A single **content extractor** walks the block tree, using Markdown text and the text of other blocks. It produces excerpts, `search_text` (for full-text search), RSS/Atom content (rendered HTML of the blocks) and reading time.
  - Revisions, scheduling and publishing are identical for pages and posts.

---

## 4. Data – PostgreSQL

- **Npgsql.EntityFrameworkCore.PostgreSQL** + **EF Core migrations** in the host project (`dotnet ef migrations add <Name> --project Homepage.Server --output-dir Data/Migrations`).
- Migrations run **at every start** and with `./Homepage.Server --migrate-only`, which the updater calls as the service user before switching releases.
- Add a **`MigrationTests`** test that fails when the model has changes without a migration (DomainNotice does this).
- **PostgreSQL 16 or newer.** The installer refuses older clusters instead of upgrading them.

### Postgres features worth using
- **Full-text search:** a generated `tsvector` column on posts with a GIN index; `websearch_to_tsquery` for the search box. Choose the text-search config (`english`, `simple`, …) per site language.
- **`jsonb`** for site settings, theme options and (optionally) block-editor content.
- **`citext`** or a unique index on `lower(slug)` for case-insensitive slugs.

### Core schema sketch
```
users          (ASP.NET Core Identity user table + valid, kind admin|member)
groups         (id, name, comment, is_system, valid, created_at, changed_at)
roles          (id, name, comment, valid, created_at, changed_at)
user_groups    (user_id, group_id, permission)     -- permission: ro|create|edit|publish|delete|rw  (OTRS group_user)
role_groups    (role_id, group_id, permission)     -- (OTRS group_role)
user_roles     (user_id, role_id)                  -- (OTRS role_user)
permission_version (id = 1, value bigint)          -- bumped on any grant change; invalidates caches
-- group_id NOT NULL on posts, media (via media_folders), menus, global_blocks, page_templates
posts          (id, type page|post, group_id, parent_id, member_group_ids int[] NULL, slug, title, excerpt,
                blocks jsonb,                                          -- published content (Markdown lives in markdown blocks)
                draft_blocks jsonb, template_id,                        -- autosaved draft
                status draft|scheduled|published, published_at, author_id,
                seo jsonb, search_text, search tsvector GENERATED,     -- search_text extracted from blocks on save
                created_at, updated_at)                                 -- + xmin for optimistic concurrency
post_revisions (id, post_id, title, blocks jsonb, schema_version, author_id, created_at)
themes         (id, name, slug, is_builtin, is_active, version, schema_version,
                tokens jsonb, variants jsonb, custom_css, created_at)
theme_versions (id, theme_id, version, snapshot jsonb, created_at)     -- roll back a theme
site_layouts   (id, theme_id, kind header|footer|page|post, blocks jsonb)
global_blocks  (id, name, blocks jsonb, updated_at)                    -- "symbols", referenced by id from block trees
page_templates (id, theme_id NULL, name, kind template|section_preset, blocks jsonb, thumbnail_media_id)
form_submissions (id, post_id, block_id, data jsonb, created_at, ip_hash)
tags, post_tags
media          (id, file_name, path, mime, width, height, size, alt_text, uploaded_by, created_at)
menus, menu_items (id, menu_id, parent_id, label, url | post_id, position)
redirects      (from_path, to_path, status_code)     -- keep old URLs working after slug changes
comments       (optional: id, post_id, author_name, body, status pending|approved|spam, ip_hash)
settings       (key, value jsonb)
audit_log      (id, user_id, action, entity, entity_id, at, ip)
```

---

## 5. Content features & libraries

| Feature | How |
|---|---|
| Drafts, scheduling | `status` + `published_at`; a `BackgroundService` publishes due posts and evicts the cache |
| Revisions | Save a `post_revisions` row on each save; diff view in the admin (DiffPlex) |
| RSS / Atom | `System.ServiceModel.Syndication`, or hand-written Razor XML templates |
| Sitemap, robots.txt | Minimal API endpoints generated from published posts |
| SEO | Per-post title/description/OpenGraph image (`seo jsonb`), canonical URLs, JSON-LD |
| Images | Originals in `data/media/`, on-demand resized/WebP variants cached on disk (**NetVips**, or **SkiaSharp**; check **ImageSharp**'s license before choosing it) |
| Email (optional) | **MailKit** for password reset and comment notifications |
| Localization | `IStringLocalizer` / resource classes if the admin should support several languages (DomainNotice ships English + Polish this way) |
| Import/export | JSON export of all content + media, which also helps migrations between servers |

---

## 6. Accounts, permission groups & security

### 6.1 Accounts
- **ASP.NET Core Identity** for **accounts only**: password hashing, cookie sign-in, lockout and TOTP 2FA. Throttle sign-ins with the rate limiter.
- Identity's own role tables are **not used** for authorization. Permissions come from the group model in 6.2.
- **First admin and recovery through the CLI**, like DomainNotice: `--create-local-account NAME` and `--reset-local-password NAME` read the password from stdin, and `install.sh` wraps them.
- **Data Protection keys** persisted to `data/keys/`, so cookies survive restarts and updates.
### 6.2 Permission groups (modelled on OTRS Community Edition)
OTRS CE authorizes agents through **groups** with **typed permissions**, assigned directly or via **roles**, and every queue (and thus every ticket) belongs to a group. Homepage uses the same model, with content areas in place of queues and CMS-specific permission types.

**Building blocks**

| OTRS | Homepage | Notes |
|---|---|---|
| Agent | **User** (admin-area account) | Identity account; `valid` / `invalid` instead of deleting |
| Group | **Group** | A named permission scope, for example `blog`, `docs`, `marketing`, plus **system groups** (below) |
| Role | **Role** | A named bundle of group permissions, for example "Blog editor" = `blog: rw` + `media: create` |
| Queue → group | **Content area → group** | Every page, post, media folder, menu, global block and template belongs to exactly one group |
| Permission types `ro`, `move_into`, `create`, `note`, `owner`, `priority`, `rw` | `ro`, `create`, `edit`, `publish`, `delete`, `rw` | Defined below |
| Agents ↔ Groups, Roles ↔ Groups, Agents ↔ Roles | Same three admin screens | Checkbox matrices, toggled with htmx |
| Customer groups (optional feature) | **Member groups** (optional) | Restrict *published* content to signed-in site members |

**Permission types** (per user or role, per group)

| Permission | Allows, on items in that group |
|---|---|
| `ro` | See items in the admin, including drafts and revisions; preview |
| `create` | Create new items in the group, and edit and delete **their own unpublished** items |
| `edit` | Edit **anyone's** items in the group (drafts only; publishing needs `publish`) |
| `publish` | Publish, unpublish and schedule items; restore revisions |
| `delete` | Delete (move to trash) and restore anyone's items in the group |
| `rw` | **Everything above**, plus managing the group's items: moving them to another group where the user also has `create` (the counterpart of OTRS `move_into`) |

**Effective permissions**, as in OTRS:
- They are **additive only.** A user's permission on a group is the **union** of direct user ↔ group grants and the grants of every valid role the user has. There are **no deny rules**, which keeps the model easy to reason about.
- `rw` implies every other type.
- **Invalid** users, groups and roles contribute nothing, and an invalid user cannot sign in.
- A user with **no permission** on a group cannot see its items at all: they are left out of lists, search, the editor's link picker and the media picker.

**System groups** gate the admin modules. OTRS calls these the module's `Group` / `GroupRo` registration (for example `admin`, `stats`, `users`). They ship built in and cannot be deleted:

| Group | `ro` | `rw` |
|---|---|---|
| `admin` | — | Users, groups, roles, the permission matrices, site settings, updates, backups. Full access everywhere, like OTRS `admin` |
| `themes` | View themes | Edit, import and activate themes; custom CSS |
| `layout` | — | Header, footer, post layout, menus |
| `html` | — | Use the raw HTML / embed blocks |
| `forms` | View form submissions | Export and delete submissions |
| `audit` | View the audit log | — |

**Defaults:**
- The installer creates the system groups, a `content` group that owns all content until you add more, and these **roles**, which are editable like any other:
  - **Administrator:** `admin: rw`
  - **Editor:** `content: rw`, `layout: rw`, `themes: ro`
  - **Author:** `content: create`
  - **Reviewer:** `content: ro` + `publish`
- `--create-local-account` puts the first account in the `admin` group, and `--reset-local-password` stays the recovery path.

**Where items get their group:**
- A new page **inherits its parent page's group**. A new post takes the **default group of its section/blog**, or the user picks from the groups where they have `create`.
- Moving an item to another group needs `rw` on the source group and `create` on the target.
- Media folders have groups too. Inserting media into a page only needs `ro` on the media's group.

**Implementation:**
- **Resource-based authorization** through ASP.NET Core's `IAuthorizationService`, for example `AuthorizeAsync(user, post, Perm.Publish)`, implemented by an `IAuthorizationHandler` that looks up the item's group.
- **Minimal API endpoints** declare module access, for example `.RequireGroup("themes", Perm.Rw)`.
- **The editor checks every operation** (`move`, `update`, `delete`, `insert`): editing the item, using that block type, and referencing that global block or media item.
- **Permissions are cached per user** in memory under a **permission version stamp** that any change to users, groups, roles or grants increments, so changes apply on the next request. They are **not** stored in the cookie, so revoking access takes effect immediately.
- List and search queries **filter by group in SQL**, by joining the user's effective group set, instead of filtering in memory.
- Every grant change is written to the **audit log**: who changed which user, group or role, from what to what.

**Optional: external groups.** OTRS can sync LDAP groups to its groups and roles. If you later sign in through OIDC (Authentik/Keycloak) or LDAP, map external group claims → Homepage **roles** at sign-in, refreshed on each sign-in.

**Optional: member groups** (the counterpart of OTRS customer groups) are for sites with members-only content:
- Site members are a separate kind of account that cannot reach the admin.
- A published page can be limited to member groups.
- Such pages are **excluded from the shared output cache** (or cached varied by member group) and from the sitemap and RSS.

### 6.3 Security
- **Antiforgery with htmx:** render the token into a `<meta>` tag and send it as a header on every htmx request:
  ```js
  document.body.addEventListener('htmx:configRequest', e => {
    e.detail.headers['RequestVerificationToken'] =
      document.querySelector('meta[name="csrf-token"]').content;
  });
  ```
  The token is per visitor, so it belongs **only in the admin layout** (which is never output-cached). On cached public pages, load the comment form as an **uncached htmx fragment** (`hx-get="/posts/42/comment-form" hx-trigger="revealed"`) that carries its own token. A response that sets the antiforgery cookie is not cached anyway.
- **XSS:** Razor encodes by default. Sanitize Markdown output if raw HTML is allowed, and validate link URLs (`http`/`https`/relative only).
- **Risky blocks:** the **raw HTML** and **embed** blocks and **custom CSS** need `rw` in the `html` / `themes` system groups (6.2). Embeds are limited to an allow-list of providers (YouTube, Vimeo, maps) and sandboxed iframes. The server enforces this on every editor operation, not only by hiding blocks in the palette.
- **Canvas iframe:** same origin. Send `X-Frame-Options: SAMEORIGIN` / `frame-ancestors 'self'` on admin and canvas routes, and `frame-ancestors 'none'` on the public site unless embedding is wanted. Validate the origin on every `postMessage` handler.
- **Theme imports:** validate the `.zip` (no path traversal, only allowed file types and sizes). Strip `@import`/`url()` pointing off-site from custom CSS, or allow it only for `admin: rw`.
- **Uploads:** check the file type by content (not just the extension), cap the size, store under random names, and never execute or serve uploads as HTML. Set a **Content-Security-Policy** header.
- **Reverse proxy:** since nothing uses WebSockets, any plain HTTP proxy works. Enable forwarded headers with `KnownProxies` so rate limits and the audit log see real client IPs.

---

## 7. Development & testing (Docker is fine here)

**Local development**
- `docker compose up -d db` starts PostgreSQL 16, optionally with **Adminer**/pgAdmin and **Mailpit** for test email.
- `dotnet watch` on the server, and the Tailwind CLI in `--watch` mode next to it.
- `appsettings.Development.json` + user secrets for the connection string. The build removes this file from release packages.

**Tests**
- **xUnit** (Microsoft.Testing.Platform, set in `global.json`) + **`WebApplicationFactory`** for endpoint tests.
- **Testcontainers for .NET** starts a real PostgreSQL 16 for integration tests, the same way DomainNotice's server tests run in CI.
- **AngleSharp** to assert on HTML fragments.
- **Playwright for .NET** end-to-end tests of admin flows, **especially the visual editor**: drag a block from the palette, reorder, nest, undo/redo, edit in the inspector, publish, switch theme, check responsive preview.
- **Permission tests:** a matrix test (user × group × permission × action) covering direct grants, role grants, invalid users/groups/roles, `rw` implying everything, and moves between groups. It also checks that list and search queries never leak items from groups the user can't see.
- **Block tests:**
  - golden JSON fixtures per schema version (3.6);
  - a render test for every block type × every built-in theme;
  - an operation test for every editor op and its inverse.
- **Installer and update tests in Docker** (`deploy/linux/tests/`), copied from the DomainNotice approach:
  - `container-run-smoke.sh`: fresh install on `ubuntu:24.04` with a real PostgreSQL; the server starts in Production and pages load.
  - `container-smoke.sh`: install, upgrade and repair paths, plus tampered or linked files planted by the service user.
  - `container-maint-smoke.sh`: backup, restore, password reset, uninstall, and rejection of unsafe archives.
  - `container-sharedpg-smoke.sh`: a PostgreSQL shared with another app is left untouched.
  - `update.sh --self-test` + `update-functions-test.sh`: unit tests for the shell validation functions.
- **`testenv/`**: an Ubuntu 24.04 container with **real systemd**. `install.sh` runs inside it, so you can try self-updates end to end (`testenv/up.sh`, `testenv/reset.sh`).

---

## 8. Production: install, update & build workflow (from DomainNotice)

Production uses **no Docker** and installs **no .NET runtime**. The server is published **self-contained** for `linux-x64` and `linux-arm64` and runs as a hardened **systemd** service on **Ubuntu 24.04 LTS or newer**. This workflow is taken from [BarPos/DomainNotice](https://github.com/BarPos/DomainNotice) (`build/build-server.sh`, `deploy/linux/`, `.github/workflows/`, `docs/server/`) and renamed for Homepage.

Adaptations that are **not** in DomainNotice:
- the Tailwind build step;
- `--port` / `--mode` (DomainNotice: `--console-port` / `--console-mode`);
- media uploads in `data/`;
- the public-site notes in 8.4;
- release signing (8.9).

There is no agent or MSI part.

### 8.1 Versioning
- **One version** for the whole product: `<Version>` in `Directory.Build.props`, mirrored in `AppVersion.Current` (`Homepage.Shared/AppVersion.cs`). A unit test and the build script both fail if they differ.
- Versions are **plain `x.y.z`**, and the tag is `v<version>`.
- **Central package management** in `Directory.Packages.props`, and the SDK pinned in `global.json`.

### 8.2 Build: `build/build-server.sh`
Works on Linux and macOS (cross-publish). Produces in `artifacts/`:
```
Homepage.Server-<version>-linux-x64.tar.gz
Homepage.Server-<version>-linux-arm64.tar.gz
install.sh
SHA256SUMS.txt
```
Steps:
1. Read and validate the version from `Directory.Build.props` and check it matches `AppVersion.cs`.
2. Build the CSS (Tailwind CLI).
3. `dotnet publish Homepage.Server -c Release -r linux-<arch> --self-contained true -p:DebugType=None -p:DebugSymbols=false -p:Version=<version>`.
4. Remove `appsettings.Development.json`, then copy `deploy/linux/` (`install.sh`, `update.sh`, `backup.sh`, systemd units) into the package.
5. Refuse links and special files, normalize modes (dirs 755, files 644, executables 755).
6. Pack with **root-owned entries and no macOS metadata** (GNU tar `--owner=0 --group=0 --sort=name`, or bsdtar `--uid 0 --gid 0 --no-xattrs --no-mac-metadata`). The program files sit at the tarball root, with no top-level folder.
7. Write `SHA256SUMS.txt` over the tarballs and `install.sh`.

### 8.3 CI / release: GitHub Actions
- **`build.yml`** runs manually and is also called by `release.yml`. Jobs:
  - **version:** validate the version.
  - **test:** `bash -n` + shellcheck (non-blocking) on all scripts, `update.sh --self-test`, `dotnet build`, `dotnet test` with Testcontainers.
  - **server:** `build-server.sh --arch x64 --arch arm64`, check the asset names, and check the tarball contains the expected files (for example `wwwroot/css/site.css`). Upload as artifacts.
- **`release.yml`** runs manually (`workflow_dispatch`, with *prerelease* and *draft* inputs):
  1. **Preflight:** fail if tag `v<version>` already exists ("bump the version first").
  2. Run `build.yml`.
  3. Download the artifacts and write `SHA256SUMS.txt` over all assets.
  4. `gh release create v<version> --target $GITHUB_SHA --generate-notes [--prerelease] [--draft]` with all assets.
- **Release process:** bump the version in both places → commit and push to `main` → **Actions → release → Run workflow**.

### 8.4 Install: `install.sh`
```sh
curl -fsSLO https://github.com/<owner>/Homepage/releases/latest/download/install.sh
sudo bash install.sh                                   # latest release for this CPU
sudo bash install.sh --version 1.2.0                   # a specific release
sudo bash install.sh --package Homepage.Server-1.2.0-linux-x64.tar.gz   # offline
```
- Downloads from the GitHub releases of `GITHUB_REPOSITORY` (a variable at the top of the script), and **checks every download against `SHA256SUMS.txt`** before using anything from it.
- **Interactive or `--unattended`.** Flags include:
  - database: `--db=local|existing`, `--db-host/--db-port/--db-name/--db-user/--db-sslmode`
  - site: `--server-name`, `--port`, `--cert/--cert-key`, `--mode=https|http`, `--admin-user`, `--open-firewall=yes|no`
- **Secrets never as flags.** They come from `--secrets-file` (mode 600), `--secrets-stdin`, environment variables or a hidden prompt.
- **PostgreSQL, as a polite guest:**
  - `--db=local` installs PostgreSQL if missing. If a cluster already exists, it **only adds its own role and database** (`homepage`, `homepage_2`, …).
  - It never alters `postgresql.conf`/`pg_hba.conf`, never upgrades or restarts the cluster, and refuses clusters older than 16.
  - The role gets no superuser, createdb or createrole rights, and **owns** its database, so rollbacks can restore it.
  - A failed install removes only what that run created.
- **HTTPS:**
  - `https` mode: Kestrel serves the given PEM cert/key, or a generated self-signed one.
  - `http` mode: for running behind your own reverse proxy (Caddy/nginx with Let's Encrypt). This is the usual choice for a public website.
- **Differences for a public website.** DomainNotice is an internal LAN tool, so adjust these:
  - **Recommended production shape:** `--mode=http` with Kestrel bound to **`127.0.0.1`** (DomainNotice writes `0.0.0.0`), behind **Caddy** or nginx on the same host for Let's Encrypt with automatic renewal.
  - **`https` mode** with a self-signed or static certificate has **no renewal**. Use it only if you add renewal yourself (for example a certbot deploy hook that copies the cert into `config/` and restarts the service).
  - **`/healthz` "loopback only"** is meaningless behind a same-host proxy, because every proxied request comes from 127.0.0.1. Run forwarded-headers processing *before* the loopback check, or block `/healthz` in the proxy (do both). `update.sh` calls Kestrel directly via `HEALTH_URL=http://127.0.0.1:<port>/healthz`.
  - Open only 80/443 in the firewall (the proxy). The Kestrel port stays local.
- **Installed layout** (`/opt/homepage`):

| Path | Owner / mode | Content |
|---|---|---|
| `releases/<version>/` | root, 755 | Program files (read-only for the service) |
| `current` | symlink | The running release |
| `config/appsettings.Production.json` | root:homepage 640 | Configuration (via `HOMEPAGE_CONFIG`) |
| `config/site.pem`, `site.key` | root:homepage 640 | TLS certificate (https mode) |
| `config/update.env` | root 600 | DB settings + `HEALTH_URL` for `update.sh` |
| `data/` | homepage 700 | **Media uploads**, Data Protection keys, caches, staged updates (the only writable path) |
| `backups/` | root 700 | `backup.sh` archives; `pre-update/` holds the dump taken before each update |
| `bin/update.sh`, `bin/backup.sh` | root 755 | Root-run scripts, replaced only by `install.sh` from a verified package |
| `/etc/systemd/system/homepage.service` | | The server |
| `/etc/systemd/system/homepage-update.{path,service}` | | Applies staged self-updates as root |

- **Maintenance menu.** Running `install.sh` again on an installed server offers these, each also available as a flag:
  - `--repair`: re-apply owners and modes, restart; never touches config or data.
  - `--upgrade --package FILE`: install a newer release through the same path as a self-update.
  - `--backup [--output PATH]`, `--restore FILE`
  - `--reset-password [USER]`
  - `--uninstall`: everything, including the DB; asks you to type `uninstall` and offers a backup first. `--keep-data` keeps config, data and the DB.

### 8.5 The systemd service
`homepage.service`, copied from `domainnotice.service`:
- `Type=notify` (`UseSystemd()`), `User=homepage`, `WorkingDirectory`/`ExecStart` under `/opt/homepage/current`.
- `Environment=HOMEPAGE_CONFIG=…/appsettings.Production.json`, `ASPNETCORE_ENVIRONMENT=Production`.
- `TimeoutStartSec=300` (migrations run at start) and `Restart=on-failure`.
- `AmbientCapabilities=CAP_NET_BIND_SERVICE`, so it can bind 80/443 without root.
- **Hardening:**
  - filesystem: `ProtectSystem=strict` + `ReadWritePaths=/opt/homepage/data`, `ProtectHome`, `PrivateTmp`, `PrivateDevices`
  - kernel and processes: `ProtectKernel*`, `ProtectProc=invisible`, `RestrictNamespaces`, `RestrictSUIDSGID`, `RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK`, `NoNewPrivileges`, `UMask=0077`
  - **Not** `MemoryDenyWriteExecute`, because it breaks the .NET JIT.
- Logs go to the journal: `journalctl -u homepage -f`.

### 8.6 Self-update from the admin ("Update now")
**Settings → About & updates** shows the current and latest version with release notes, plus **Check now** and **Update now**. The server **never needs root**:

1. A `BackgroundService` checks GitHub releases every `Updates:CheckIntervalHours`. It uses `/releases/latest` on the `stable` channel; the `prerelease` channel takes the newest non-draft release.
2. On **Update now**, the server downloads the tarball for its CPU plus `SHA256SUMS.txt`, verifies the hash, and stages both in `data/updates/<version>/`.
3. It atomically writes `data/updates/request.conf` (version, from_version, arch, file, sha256, run_id).
4. **`homepage-update.path`** (`PathExists=`) starts the one-shot **`homepage-update.service`**, which runs the root-owned `/opt/homepage/bin/update.sh`. That is never a script from the new package.
5. `update.sh` then:
   - removes the request and parses it **without eval**, validating every value: plain `x.y.z`, strictly newer, correct arch;
   - re-checks the hash;
   - **unpacks safely**: no absolute paths, no `..`, no links pointing outside, no special files, root-owned;
   - stops the service and takes a **`pg_dump`** into `backups/pre-update/`;
   - runs `--migrate-only` **as the service user** (EF migrations **and block/theme content migrations**, 3.6), switches the `current` symlink, starts the service;
   - **waits up to 2 minutes for `/healthz`**.
6. **Rollback:** on failure it restores the dump, switches the symlink back and starts the old version.
7. It writes `data/updates/result.conf` (`Succeeded`/`RolledBack`/`Failed` + message), which the admin shows after restart. The last **3 releases** are kept. Downgrades are not offered.

The root scripts and units are **not** replaced by a self-update, because nothing staged by the service process is trusted as root. Refresh them with `install.sh --repair --version X.Y.Z` or `--upgrade`.

**Configuration:**
```json
"Updates": { "GitHubRepository": "<owner>/Homepage", "Channel": "stable", "CheckIntervalHours": 24, "GitHubToken": "" }
```
A blank `GitHubRepository` turns updates off, and the server then makes **no outbound calls**.

Self-update is enabled only when the server detects the `install.sh` layout: Linux x64/arm64, Ubuntu 24.04+, running from `/opt/homepage/releases/<v>/`, `current` is a symlink, and the path unit exists. Otherwise the admin explains why.

### 8.7 Backup & restore: `backup.sh`
- `backup.sh create [--output DIR]` makes one archive (mode 600, root) containing:
  - a `pg_dump` of **Homepage's own database only**;
  - `config/`;
  - `data/`, which here includes **all media uploads**;
  - the running `releases/<version>/`.
- `backup.sh list` and `backup.sh restore FILE`. Restore validates the archive first: no paths outside the expected folders, no hard links, devices, FIFOs or setuid entries. It also takes a safety backup of the current state and asks you to type `restore`.
- `--output` must be a root-owned, non-writable-by-others folder. `/tmp` is refused.
- **Moving servers:** install the same version on the new machine, then restore.
- Nightly cron example: `0 3 * * * root /opt/homepage/bin/backup.sh create --output /mnt/nas/homepage/`.

### 8.8 Server CLI
```sh
cd /opt/homepage/current
sudo -u homepage env HOMEPAGE_CONFIG=/opt/homepage/config/appsettings.Production.json \
     ASPNETCORE_ENVIRONMENT=Production ./Homepage.Server <option>
```

| Option | |
|---|---|
| `--migrate-only` | Apply pending EF migrations, then block/theme content migrations, and exit |
| `--create-local-account NAME` | Create an admin (password read from stdin) |
| `--reset-local-password NAME` | Reset a password and enable the account |
| `--list-local-accounts` | List accounts |

### 8.9 Known gap carried over
DomainNotice's releases are **not signed**: `SHA256SUMS.txt` catches corruption, not a compromised repository. For Homepage, consider signing `SHA256SUMS.txt` with **minisign** or **cosign** and pinning the public key in `install.sh` and the updater.

---

## Project layout

```
Homepage/
├── Homepage.sln
├── global.json                    # SDK pin + test runner
├── Directory.Build.props          # single <Version>, shared settings
├── Directory.Packages.props       # central package versions
├── Homepage.Shared/               # AppVersion.cs, shared types
├── Homepage.Core/                 # domain: Data, Content, Blocks (models, registry, ops, content migrations),
│                                  #         Themes (tokens, CSS generation, import/export), Media, Accounts,
│                                  #         Permissions (groups, roles, grants, resolver, auth handlers), Updates, Jobs
├── Homepage.Web/                  # Razor components + endpoints
│   ├── Blocks/                    #   one .razor renderer per block type (shared by site + editor canvas)
│   ├── Site/                      #   public layouts, pages
│   ├── Admin/                     #   admin pages, inspector forms, theme editor
│   ├── Editor/                    #   editor endpoints (canvas, ops, fragments, publish)
│   └── wwwroot/                   #   htmx, alpine, sortable, tiptap (vendored); admin/editor/*.js (plain ES modules)
├── themes/                        # built-in themes (theme.json, fonts, presets), packed into the release
├── Homepage.Server/               # host: Program.cs, CLI options, Data/Migrations, appsettings*.json
├── Homepage.Tests/                # xUnit + Testcontainers (incl. MigrationTests, VersionTests)
├── build/
│   └── build-server.sh
├── deploy/linux/
│   ├── install.sh  update.sh  backup.sh
│   ├── homepage.service  homepage-update.path  homepage-update.service
│   └── tests/                     # container smoke tests (Docker, ubuntu:24.04)
├── testenv/                       # Ubuntu + real systemd container for end-to-end install/update tests
├── docker-compose.yml             # dev only: PostgreSQL (+ Adminer, Mailpit)
├── docs/                          # GitHub Pages docs (installation, updates, operations, configuration)
├── .github/workflows/
│   ├── build.yml
│   └── release.yml
└── techstack.md
```

---

## Decision summary

1. **.NET 10 LTS, ASP.NET Core Minimal APIs, static Razor components.** No Blazor render modes and no SignalR.
2. **Public site:** cached server-rendered HTML. **Admin:** **htmx + Alpine.js**. Pages and posts are both block trees. **Markdown is a block** (Markdig, live preview) that mixes freely with visual blocks.
3. **Visual drag-and-drop page builder.** Pages are block trees in `jsonb`, rendered by the same Razor components on the site and in the editor's iframe canvas. The editor is a custom build with SortableJS, htmx and Alpine; GrapesJS with custom components is the fallback. Content migrations run on every update.
4. **Themes:** design tokens, block variants, header/footer layouts and fonts, with a live theme editor, import/export and versioning. **Tailwind (standalone CLI)** classes map to CSS variables, so a theme can switch at runtime.
5. **PostgreSQL 16+ with EF Core.** Full-text search, revisions, redirects, scheduled publishing.
6. **Identity** accounts with 2FA, and **OTRS-style permission groups**: content belongs to groups, users get typed permissions (`ro`…`rw`) directly or via roles, and system groups gate the admin modules. Permissions are additive only. Recovery through the CLI.
7. **Docker for development and testing only:** Compose, Testcontainers, installer smoke tests, a systemd test environment.
8. **Production as DomainNotice does it:**
   - self-contained tarballs built by `build-server.sh` and released by manual GitHub Actions workflows;
   - `install.sh` on Ubuntu 24.04+ and a hardened systemd service;
   - admin-triggered self-update through a root path unit with a `pg_dump` and health-check rollback;
   - `backup.sh` for backup and restore.

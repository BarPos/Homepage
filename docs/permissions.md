---
title: Permissions
nav_order: 6
---

# Permissions
{: .no_toc }

1. TOC
{:toc}

Homepage uses the permission model of **OTRS Community Edition**: users get typed permissions on **groups**, directly
or through **roles**, and every piece of content belongs to exactly one group.

## Building blocks

| OTRS | Homepage | |
|---|---|---|
| Agent | **User** | An admin account. *Valid* or *invalid* instead of deleting. |
| Group | **Group** | A permission scope, for example `blog`, `docs`, `marketing`, plus the **system groups** below |
| Role | **Role** | A named bundle of group permissions, for example "Blog editor" = `blog: rw` |
| Queue → group | **Content → group** | Every page, post and media file belongs to one group |
| Agents ↔ Groups, Roles ↔ Groups, Agents ↔ Roles | The same three screens | Under **Users & permissions** |

## Permission types

Set per user (or role) and per group:

| Permission | Allows, on items in that group |
|---|---|
| `ro` | See items in the admin, including drafts and revisions |
| `create` | Create items, and edit and delete **their own items that were never published** |
| `edit` | Edit anyone's items (the draft; publishing, and changing the title, address or blog of a published item, needs `publish`) |
| `publish` | Publish and unpublish items |
| `delete` | Delete anyone's items |
| `rw` | **Everything above**, and moving items to another group where you have `create` |

## Effective permissions

- **Additive only.** A user's permissions on a group are the union of their direct grants and the grants of every
  valid role they have. There are no deny rules.
- `rw` implies every other type.
- **Invalid** users, groups and roles count for nothing; an invalid user cannot sign in.
- A user with **no permission** on a group does not see its items anywhere: lists, search in the admin, the editor
  (`404`), the media picker.
- `admin: rw` gives every permission on every group.
- A user with no permission on any group cannot sign in.

**Users & permissions → Users → Edit** shows a user's effective permissions as a table.

## System groups

They decide who may use parts of the admin. They are created at install, cannot be renamed or made invalid, and are
never used for content.

| Group | `ro` | `rw` |
|---|---|---|
| `admin` | — | Users, groups, roles, permissions, settings, updates. Full access everywhere. |
| `themes` | See themes and previews | Activate, duplicate and edit themes; custom CSS |
| `layout` | See themes and previews | Header, footer, menus and blogs |
| `html` | — | Use the raw HTML block |
| `forms` | — | Reserved for form submissions (not built yet) |
| `audit` | Read the audit log | — |
| `analytics` | See visitor statistics | Change analytics settings, delete the data |

## Defaults

The installer creates the system groups, a **`content`** group that owns all content at first, and four roles. Roles
are ordinary data: change, rename or make them invalid as you like.

| Role | Grants |
|---|---|
| **Administrator** | `admin: rw` |
| **Editor** | `content: rw`, `layout: rw`, `themes: ro` |
| **Author** | `content: create`, `content: ro` |
| **Reviewer** | `content: ro`, `content: publish` |

The first account (from the installer or `--create-local-account`) gets a direct `admin: rw` grant. New users created
in the admin get nothing until you assign a role or a grant.

## The screens

| Screen | |
|---|---|
| **Users** | **Add account** (user name, display name, email, password), set valid/invalid, change display name and email, reset passwords, turn off two-factor sign-in, see effective permissions |
| **Groups** | Create, rename and comment groups; make them invalid. Names are lower case letters, digits, `-` and `_`. |
| **Roles** | Create, rename and comment roles; make them invalid |
| **Users ↔ Groups** | Pick a user: a table of groups × permission types. Each checkbox saves at once. |
| **Roles ↔ Groups** | The same for a role |
| **Users ↔ Roles** | A table of users × roles |

Every change is written to the audit log and takes effect on the affected users' next request. You cannot remove your
own direct `admin: rw` or make your own account invalid.

## Where new items go

- A new item goes to the group chosen when creating it (the list offers groups where you have `create`). **Settings →
  Default group** preselects one.
- **Moving** an item to another group (item settings → Group) needs `rw` on its current group and `create` on the
  target.
- Inserting an image only needs `ro` on the image's group.

## Examples

**A team blog with an approval step.** Group `blog`. Role *Writer* = `blog: create` + `blog: ro`; role *Editor in
chief* = `blog: rw`. Writers draft and edit their own posts until they are published; only the editor in chief
publishes and edits published posts.

**A docs section that marketing must not touch.** Groups `docs` and `marketing`. The docs team has `docs: rw`,
marketing has `marketing: rw`. Neither sees the other's pages in the admin; both are on the same public site.

**An agency that edits pages but not the design.** Give them `content: rw` only. Without `themes` and `layout` they
cannot change colors, header, footer or menus, and without `html: rw` they cannot add raw HTML.

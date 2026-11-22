---
title: Development
nav_order: 9
---

# Development
{: .no_toc }

1. TOC
{:toc}

## Requirements

- .NET 10 SDK (see `global.json`)
- Docker, for development and tests only (PostgreSQL in Compose and through Testcontainers). Production installs never
  use Docker.
- Bash 4 or newer for the script tests (`update.sh --self-test`); macOS ships bash 3.2, so install a newer one.

## Run the server locally

```sh
docker compose up -d db                       # PostgreSQL 16 on 127.0.0.1:5433 (homepage / homepage-dev)
cd Homepage.Server
dotnet run -- --environment Development --migrate-only
echo 'a-long-password' | dotnet run -- --environment Development --create-local-account admin
dotnet run -- --environment Development       # site: http://127.0.0.1:5080, admin: /admin
```

Development settings are in `Homepage.Server/appsettings.Development.json`: the Compose database, data in `.devdata/`
at the repository root, and **no update checks** (`Updates:GitHubRepository` is empty). `docker compose --profile
tools up -d` adds Adminer on http://127.0.0.1:8081.

Static files (`Homepage.Web/wwwroot`) are served from the source folder, so CSS and JavaScript changes need only a
browser reload. Razor and C# changes need a restart.

## Tests

```sh
dotnet test --project Homepage.Tests                   # needs Docker (PostgreSQL 16 via Testcontainers)
deploy/linux/update.sh --self-test && deploy/linux/tests/update-functions-test.sh
```

Set `HOMEPAGE_TEST_PG` to an admin connection string to use an existing PostgreSQL instead of Docker. Run a subset
with `dotnet test --project Homepage.Tests -- --filter-class "*PermissionTests"`.

| Test | Covers |
|---|---|
| `UnitTests.cs` | Permission rules and the content-rule matrix, block operations and nesting, props validation and binding, content migrations, theme tokens and CSS, update request file, upload sniffing, Markdown sanitizing, slugs, links |
| `PostgresTests.cs` | Migrations match the model and seeding is idempotent; the permission resolver with roles and invalid users, groups and roles; lists never leak items from other groups; author and reviewer rules; full-text search |
| `EndToEndTests.cs` | The real host over HTTP: sign in, antiforgery, create, edit through the editor endpoints, publish, view; an Author's limits, media upload into an image block, menus, reserved addresses, revoking a role |

## Database migrations

After changing the model in `Homepage.Core/Data`:

```sh
dotnet ef migrations add <Name> --project Homepage.Server --output-dir Data/Migrations
```

`PostgresTests.MigrationsMatchTheModelAndSeedIsIdempotent` fails while the model has changes that are missing from the
migrations.

When a block's props change shape, add a content migration as well (see
[Content migrations](reference/content-format.md#content-migrations)).

## Adding a block type

1. A props class in `Homepage.Core/Blocks/BlockProps.cs`, with `[Display]`, `[Range]`, `[StringLength]` and the
   Homepage attributes (`[ThemeColor]`, `[MediaPicker]`, `[MenuPicker]`, `[SafeUrl]`). The inspector form is generated
   from it.
2. A `BlockDefinition` in `BlockRegistry` (label, icon, category, container rules, required system group).
3. A Razor component in `Homepage.Web/Blocks/` and an entry in `BlockComponents`.
4. Styles in `site.css`, using only theme variables.
5. If it holds text, add it to `ContentExtractor` for search and excerpts.

## Building packages

| Command | Output |
|---|---|
| `build/build-server.sh` | `artifacts/Homepage.Server-<version>-linux-x64.tar.gz`, `-linux-arm64.tar.gz`, `install.sh`, `SHA256SUMS.txt` |
| `build/build-server.sh --arch x64 --output DIR` | One architecture, another folder |

It works on Linux and macOS (cross-publish), refuses links and special files, sets root-owned entries and checks that
`<Version>` matches `AppVersion.Current`. The **build** workflow (Actions → build → Run workflow) builds and tests
without making a release.

## Releasing

There is one version for everything: `<Version>` in `Directory.Build.props` and `AppVersion.Current` in
`Homepage.Shared/AppVersion.cs`. A test checks that they match.

1. Bump both, then commit and push to `main`.
2. **GitHub → Actions → release → Run workflow.** Pick the branch, and optionally *prerelease* or *draft*.
3. The workflow:
   - checks that the tag `v<version>` doesn't exist yet;
   - runs the **build** workflow (tests, server tarballs);
   - writes `SHA256SUMS.txt`;
   - creates the tag on the selected commit and the release with all assets.

`install.sh` (`GITHUB_REPOSITORY`) and the server (`Updates:GitHubRepository`) download from the releases of
`BarPos/Homepage`.

## Documentation

These pages are in `docs/` (Jekyll with [Just the Docs](https://just-the-docs.com/)). GitHub Pages builds them from
`main`, folder `/docs` (repository **Settings → Pages → Deploy from a branch**). Local preview:

```sh
cd docs && bundle install && bundle exec jekyll serve      # http://127.0.0.1:4000/Homepage/
```

## Code layout conventions

- Logic lives in `Homepage.Core/<Area>/`; `AddHomepageCore` registers it. Rendering and endpoints live in
  `Homepage.Web`; `AddHomepageWeb` / `UseHomepageWeb` wire them up.
- Pages are Razor components rendered statically with `RazorComponentResult`; never add a render mode.
- Every permission check happens in a service, not only in a component.
- Package versions are central in `Directory.Packages.props`.

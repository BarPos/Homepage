using System.Security.Claims;
using Homepage.Accounts;
using Homepage.Blocks;
using Homepage.Common;
using Homepage.Content;
using Homepage.Data;
using Homepage.Media;
using Homepage.Permissions;
using Homepage.Themes;
using Homepage.Updates;
using Homepage.Web.Admin;
using Homepage.Web.Editor;
using Homepage.Web.Rendering;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;

namespace Homepage.Web.Endpoints;

public static class AdminEndpoints
{
    public sealed record OpRequest(string Op, string? Id, string? Type, string? ParentId, int Index);

    public static void MapAdmin(this WebApplication app)
    {
        // ---------------------------------------------------------------- sign-in (anonymous)
        var anon = app.MapGroup("/admin").AddEndpointFilter<AdminErrorFilter>().DisableAntiforgery();

        anon.MapGet("/login", (HttpContext http, IAntiforgery af, string? returnUrl, int? denied) =>
            Html.Render<LoginPage>(new
            {
                Token = af.GetAndStoreTokens(http).RequestToken ?? "",
                ReturnUrl = returnUrl,
                Error = denied == 1 ? "This account has no access to the admin area." : null,
            }));

        anon.MapPost("/login", async (HttpContext http, IAntiforgery af, UserManager<User> users, IPermissionService perms, IAuditLog audit,
            [FromForm] string userName, [FromForm] string password, [FromForm] string? returnUrl) =>
        {
            if (!await af.IsRequestValidAsync(http)) return Results.BadRequest();
            const string fail = "Wrong user name or password, or the account is locked.";
            var user = await users.FindByNameAsync(userName.Trim());
            var ip = http.Connection.RemoteIpAddress?.ToString();
            if (user is null || !user.Valid || await users.IsLockedOutAsync(user))
            {
                await audit.WriteAsync(userName, "signin.failed", "User", user?.Id.ToString() ?? "", "", ip);
                return Html.Render<LoginPage>(new { Token = af.GetAndStoreTokens(http).RequestToken ?? "", Error = fail, UserName = userName }, 401);
            }
            if (!await users.CheckPasswordAsync(user, password))
            {
                await users.AccessFailedAsync(user);
                await audit.WriteAsync(userName, "signin.failed", "User", user.Id.ToString(), "", ip);
                return Html.Render<LoginPage>(new { Token = af.GetAndStoreTokens(http).RequestToken ?? "", Error = fail, UserName = userName }, 401);
            }
            await users.ResetAccessFailedCountAsync(user);
            if (!(await perms.GetAsync(user.Id)).CanSeeAdmin)
                return Html.Render<LoginPage>(new { Token = af.GetAndStoreTokens(http).RequestToken ?? "", Error = "This account has no permissions yet. Ask an administrator.", UserName = userName }, 403);
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.UserName ?? ""),
                new Claim("display_name", user.DisplayName),
            ], AdminAuth.Scheme);
            await http.SignInAsync(AdminAuth.Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
            await audit.WriteAsync(user.UserName ?? "", "signin", "User", user.Id.ToString(), "", ip);
            return Results.Redirect(returnUrl is { Length: > 1 } r && r.StartsWith("/admin", StringComparison.Ordinal) && !r.StartsWith("//", StringComparison.Ordinal) ? r : "/admin");
        }).RequireRateLimiting("signin");

        // ---------------------------------------------------------------- signed-in area
        var admin = app.MapGroup("/admin")
            .RequireAuthorization(p => p.AddAuthenticationSchemes(AdminAuth.Scheme).RequireAuthenticatedUser())
            .AddEndpointFilter<AdminErrorFilter>()
            .AddEndpointFilter<AdminFilter>()
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));

        admin.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(AdminAuth.Scheme);
            return Results.Redirect("/admin/login");
        });

        admin.MapGet("", async (HttpContext http, ContentService content, ThemeService themes, IDbContextFactory<AppDbContext> dbf) =>
        {
            var a = AdminContext.Get(http);
            var items = await content.ListAsync(a.Perms, null, null, null, 1000);
            await using var db = await dbf.CreateDbContextAsync();
            var groups = await db.Groups.AsNoTracking().ToListAsync();
            var site = await SiteSettings.LoadAsync(db);
            return Html.Render<DashboardPage>(new
            {
                Pages = items.Count(i => i.Type == ContentType.Page),
                Posts = items.Count(i => i.Type == ContentType.Post),
                Drafts = items.Count(i => i.Status == ContentStatus.Draft),
                ThemeName = (await themes.GetActiveAsync()).Name,
                Recent = items.Take(8).ToList(),
                GroupNames = groups.ToDictionary(g => g.Id, g => g.Name),
                CreateGroups = CreatableGroups(a.Perms, groups),
                DefaultGroupId = site.DefaultGroupId,
            });
        });

        // ---------------------------------------------------------------- content
        admin.MapGet("/content", async (HttpContext http, ContentService content, IDbContextFactory<AppDbContext> dbf,
            string? type, string? groupId, string? q) =>
        {
            var a = AdminContext.Get(http);
            var gid = int.TryParse(groupId, out var g) ? g : (int?)null;
            var t = Enum.TryParse<ContentType>(type, ignoreCase: true, out var parsed) ? parsed : ContentType.Page;
            var items = await content.ListAsync(a.Perms, t, gid, q);
            await using var db = await dbf.CreateDbContextAsync();
            var groups = await db.Groups.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
            var visible = a.Perms.GroupsWith(Perm.Ro).ToHashSet();
            return Html.Render<ContentListPage>(new
            {
                Type = t, Items = items, Search = q, GroupId = gid,
                Groups = groups.Where(x => visible.Contains(x.Id) && !x.IsSystem).ToDictionary(x => x.Id, x => x.Name),
                CreateGroups = CreatableGroups(a.Perms, groups),
                DefaultGroupId = (await SiteSettings.LoadAsync(db)).DefaultGroupId,
            });
        });

        admin.MapPost("/content/new", async (HttpContext http, ContentService content,
            [FromForm] string title, [FromForm] string type, [FromForm] int groupId) =>
        {
            var a = AdminContext.Get(http);
            var t = Enum.TryParse<ContentType>(type, ignoreCase: true, out var parsed) ? parsed : throw new BlockException("Unknown content type.");
            var item = await content.CreateAsync(a.Perms, t, title, groupId, a.Actor);
            return Results.Redirect($"/admin/editor/content-{item.Id}");
        });

        admin.MapGet("/content/{id:int}", async (int id, HttpContext http, ContentService content, IDbContextFactory<AppDbContext> dbf) =>
            await ContentSettings(id, http, content, dbf, null));

        admin.MapPost("/content/{id:int}", async (int id, HttpContext http, ContentService content, IDbContextFactory<AppDbContext> dbf,
            IOutputCacheStore cache, [FromForm] string title, [FromForm] string slug, [FromForm] string? excerpt, [FromForm] int groupId) =>
        {
            var a = AdminContext.Get(http);
            try
            {
                var r = await content.UpdateSettingsAsync(a.Perms, id, title, slug, excerpt ?? "", groupId, a.Actor);
                if (r.AffectsSite) await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            }
            catch (Exception ex) when (ex is BlockException or ConflictException)
            {
                return await ContentSettings(id, http, content, dbf, ex.Message);
            }
            return Results.Redirect($"/admin/content/{id}");
        });

        admin.MapPost("/content/{id:int}/publish", async (int id, HttpContext http, ContentService content, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            await content.PublishAsync(a.Perms, id, a.Actor);
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            if (http.Request.Headers.ContainsKey("HX-Request")) { Flash.Ok(http.Response, "Published."); return Results.NoContent(); }
            return Results.Redirect($"/admin/content/{id}");
        });

        admin.MapPost("/content/{id:int}/unpublish", async (int id, HttpContext http, ContentService content, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            await content.UnpublishAsync(a.Perms, id, a.Actor);
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            return Results.Redirect($"/admin/content/{id}");
        });

        admin.MapPost("/content/{id:int}/delete", async (int id, HttpContext http, ContentService content, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            var item = await content.GetForViewAsync(a.Perms, id);
            var r = await content.DeleteAsync(a.Perms, id, a.Actor);
            if (r.AffectsSite) await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            return Results.Redirect($"/admin/content?type={item.Type}");
        });

        // ---------------------------------------------------------------- visual editor
        admin.MapGet("/editor/{target}", async (string target, HttpContext http, ContentService content) =>
        {
            var a = AdminContext.Get(http);
            var doc = await content.LoadDocumentAsync(a.Perms, ParseTarget(target));
            var palette = BlockRegistry.Palette(doc.Kind, a.Perms).ToList();
            var back = doc.Target.IsTheme ? "/admin/themes" : $"/admin/content?type={(doc.Kind == TreeKind.Post ? "Post" : "Page")}";
            return Html.Render<EditorPage>(new { Doc = doc, Palette = palette, BackUrl = back });
        });

        admin.MapGet("/editor/{target}/canvas", async (string target, HttpContext http, ContentService content, RenderContextFactory ctxf,
            ThemeService themes, PublicContent pub, IDbContextFactory<AppDbContext> dbf) =>
        {
            var a = AdminContext.Get(http);
            var doc = await content.LoadDocumentAsync(a.Perms, ParseTarget(target));
            var region = doc.Target.Kind == EditorTarget.ContentKind ? "main" : doc.Target.Kind;
            ActiveTheme? theme = null;
            if (doc.Target.IsTheme)
            {
                await using var db = await dbf.CreateDbContextAsync();
                theme = ThemeService.ToActive(await db.Themes.AsNoTracking().FirstAsync(t => t.Id == doc.ThemeId));
            }
            var ctx = await ctxf.CreateAsync(edit: doc.CanEdit || true, region: region, theme: theme);
            return Html.Render<CanvasPage>(new { Ctx = ctx, Main = doc.Target.IsTheme ? new BlockTree() : doc.Tree, Title = doc.Title, IsPost = doc.Kind == TreeKind.Post });
        });

        admin.MapGet("/editor/{target}/tree", async (string target, HttpContext http, ContentService content, RenderContextFactory ctxf,
            IDbContextFactory<AppDbContext> dbf) =>
        {
            var a = AdminContext.Get(http);
            var doc = await content.LoadDocumentAsync(a.Perms, ParseTarget(target));
            var region = doc.Target.Kind == EditorTarget.ContentKind ? "main" : doc.Target.Kind;
            ActiveTheme? theme = null;
            if (doc.Target.IsTheme)
            {
                await using var db = await dbf.CreateDbContextAsync();
                theme = ThemeService.ToActive(await db.Themes.AsNoTracking().FirstAsync(t => t.Id == doc.ThemeId));
            }
            var ctx = (await ctxf.CreateAsync(edit: true, region: region, theme: theme)).ForRegion(region);
            return Html.Render<CanvasRegion>(new { Ctx = ctx, Tree = doc.Tree, Contain = doc.Kind == TreeKind.Page });
        });

        admin.MapGet("/editor/{target}/layers", async (string target, HttpContext http, ContentService content) =>
        {
            var doc = await content.LoadDocumentAsync(AdminContext.Get(http).Perms, ParseTarget(target));
            return Html.Render<LayersTree>(new { Tree = doc.Tree });
        });

        admin.MapGet("/editor/{target}/inspector/{blockId}", async (string target, string blockId, HttpContext http, ContentService content,
            MediaService media, MenuService menus) =>
        {
            var a = AdminContext.Get(http);
            var doc = await content.LoadDocumentAsync(a.Perms, ParseTarget(target));
            var node = doc.Tree.Find(blockId) ?? throw new NotFoundException("The block no longer exists.");
            var def = BlockRegistry.Get(node.Type);
            var canEdit = doc.CanEdit && (def?.RequiredSystemGroup is not { } g || a.Perms.HasSystem(g, Perm.Rw));
            return Html.Render<Inspector>(new
            {
                Node = node, Target = doc.Target, CanEdit = canEdit,
                Media = await media.ListAsync(a.Perms), Menus = await menus.ListAsync(),
            });
        });

        admin.MapPost("/editor/{target}/ops", async (string target, HttpContext http, ContentService content, IOutputCacheStore cache,
            ThemeService themes, [FromBody] OpRequest op) =>
        {
            var a = AdminContext.Get(http);
            var (_, change, newId) = await content.ApplyAsync(a.Perms, ParseTarget(target), ops => op.Op switch
            {
                "insert" => ops.Insert(op.Type ?? "", op.ParentId, op.Index),
                "move" => Do(() => ops.Move(op.Id ?? "", op.ParentId, op.Index)),
                "delete" => Do(() => ops.Delete(op.Id ?? "")),
                "duplicate" => ops.Duplicate(op.Id ?? ""),
                _ => throw new BlockException($"Unknown operation '{op.Op}'."),
            }, a.Actor);
            if (change.AffectsSite) { themes.Invalidate(); await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default); }
            return Results.Json(new { ok = true, newId });
        });

        admin.MapPost("/editor/{target}/props/{blockId}", async (string target, string blockId, HttpContext http, ContentService content,
            IOutputCacheStore cache, ThemeService themes) =>
        {
            var a = AdminContext.Get(http);
            var form = await http.Request.ReadFormAsync();
            var t = ParseTarget(target);
            var doc = await content.LoadDocumentAsync(a.Perms, t);
            var node = doc.Tree.Find(blockId) ?? throw new NotFoundException("The block no longer exists.");
            var def = BlockRegistry.Get(node.Type) ?? throw new BlockException("Unknown blocks cannot be edited.");
            var props = BlockPropsBinder.Bind(def, name => form[name].LastOrDefault());
            var (_, change, _) = await content.ApplyAsync(a.Perms, t, ops => Do(() => ops.Update(blockId, props)), a.Actor);
            if (change.AffectsSite) { themes.Invalidate(); await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default); }
            http.Response.Headers["HX-Trigger"] = "hp-changed";
            return Results.NoContent();
        });

        // ---------------------------------------------------------------- media
        admin.MapGet("/media", async (HttpContext http, MediaService media, IDbContextFactory<AppDbContext> dbf) =>
        {
            var a = AdminContext.Get(http);
            await using var db = await dbf.CreateDbContextAsync();
            var groups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
            return Html.Render<MediaPage>(new
            {
                Items = await media.ListAsync(a.Perms),
                UploadGroups = CreatableGroups(a.Perms, groups),
                Groups = groups.ToDictionary(g => g.Id, g => g.Name),
            });
        });

        admin.MapPost("/media", async (HttpContext http, MediaService media, [FromForm] int groupId) =>
        {
            var a = AdminContext.Get(http);
            var form = await http.Request.ReadFormAsync();
            foreach (var file in form.Files.Take(50))
            {
                await using var s = file.OpenReadStream();
                await media.UploadAsync(a.Perms, groupId, file.FileName, s, a.Actor);
            }
            return Results.Redirect("/admin/media");
        }).WithMetadata(new RequestSizeLimitAttribute(MediaService.MaxBytes * 5));

        admin.MapPost("/media/{id:int}/delete", async (int id, HttpContext http, MediaService media) =>
        {
            var a = AdminContext.Get(http);
            await media.DeleteAsync(a.Perms, id, a.Actor);
            return Results.Redirect("/admin/media");
        });

        // ---------------------------------------------------------------- themes
        admin.MapGet("/themes", async (HttpContext http, ThemeService themes) =>
        {
            var a = AdminContext.Get(http);
            return Html.Render<ThemesPage>(new
            {
                Themes = await themes.ListAsync(a.Perms),
                CanEditThemes = a.Perms.HasSystem(SystemGroups.Themes, Perm.Rw),
                CanEditLayout = a.Perms.HasSystem(SystemGroups.Layout, Perm.Rw),
            });
        });

        admin.MapGet("/themes/{id:int}", async (int id, HttpContext http, ThemeService themes) =>
        {
            var a = AdminContext.Get(http);
            if (!a.Perms.HasSystem(SystemGroups.Themes, Perm.Rw)) throw new AccessDeniedException("Editing themes needs themes: rw.");
            var theme = await themes.GetAsync(a.Perms, id);
            if (theme.IsBuiltIn) throw new AccessDeniedException("Built-in themes are read-only. Duplicate the theme to change it.");
            return Html.Render<ThemeEditPage>(new { Theme = theme });
        });

        admin.MapPost("/themes/{id:int}", async (int id, HttpContext http, ThemeService themes, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            var form = await http.Request.ReadFormAsync();
            var tokens = new ThemeTokens
            {
                Light = ThemeTokens.AllColors.ToDictionary(c => c.Key, c => form["light." + c.Key].ToString()),
                Dark = ThemeTokens.AllColors.ToDictionary(c => c.Key, c => form["dark." + c.Key].ToString()),
                ColorMode = form["colorMode"].ToString(),
                BodyFont = form["bodyFont"].ToString(),
                HeadingFont = form["headingFont"].ToString(),
                BaseSize = Num(form["baseSize"], 17),
                Scale = Num(form["scale"], 1.25),
                LineHeight = Num(form["lineHeight"], 1.6),
                Radius = Num(form["radius"], 8),
                SpacingUnit = Num(form["spacingUnit"], 1),
                ContainerWidth = (int)Num(form["containerWidth"], 1100),
                Shadow = form["shadow"].ToString(),
                Glass = form["glass"].Contains("true"),
                GlassBlur = Num(form["glassBlur"], 18),
                GlassOpacity = Num(form["glassOpacity"], 0.62),
                Backdrop = form["backdrop"].ToString(),
            };
            var live = await themes.SaveAsync(a.Perms, id, form["name"].ToString(), tokens, form["customCss"].ToString(), a.Actor);
            if (live) await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            if (http.Request.Headers.ContainsKey("HX-Request"))
            {
                Flash.Ok(http.Response, live ? "Theme saved. The site uses the new design." : "Theme saved.");
                return Results.NoContent();
            }
            return Results.Redirect($"/admin/themes/{id}");
        });

        admin.MapGet("/themes/{id:int}/preview", async (int id, HttpContext http, ThemeService themes, RenderContextFactory ctxf, PublicContent pub) =>
        {
            var a = AdminContext.Get(http);
            var theme = await themes.GetAsync(a.Perms, id);
            var active = ThemeService.ToActive(theme);
            var ctx = await ctxf.CreateAsync(theme: active);
            var home = ctx.Site.HomePageId is { } hid ? await pub.GetPublishedByIdAsync(hid) : null;
            return Html.Render<ThemePreview>(new
            {
                Ctx = ctx,
                Tree = home is null ? new BlockTree() : BlockTree.Parse(home.Blocks),
                Css = active.Tokens.ToCss(theme.CustomCss),
            });
        });

        admin.MapPost("/themes/{id:int}/activate", async (int id, HttpContext http, ThemeService themes, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            await themes.ActivateAsync(a.Perms, id, a.Actor);
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            return Results.Redirect("/admin/themes");
        });

        admin.MapPost("/themes/{id:int}/duplicate", async (int id, HttpContext http, ThemeService themes) =>
        {
            var a = AdminContext.Get(http);
            var copy = await themes.DuplicateAsync(a.Perms, id, a.Actor);
            return Results.Redirect($"/admin/themes/{copy.Id}");
        });

        // ---------------------------------------------------------------- menus
        admin.MapGet("/menus", async (HttpContext http, MenuService menus) =>
        {
            if (!AdminContext.Get(http).Perms.HasSystem(SystemGroups.Layout, Perm.Rw)) throw new AccessDeniedException("Menus need layout: rw.");
            return Html.Render<MenusPage>(new { Menus = await menus.ListAsync() });
        });

        admin.MapPost("/menus/{key}", async (string key, HttpContext http, MenuService menus, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            var form = await http.Request.ReadFormAsync();
            var labels = form["label"];
            var urls = form["url"];
            var links = labels.Select((l, i) => new MenuLink(l ?? "", i < urls.Count ? urls[i] ?? "" : "")).ToList();
            await menus.SaveAsync(a.Perms, key, links, a.Actor);
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            return Results.Redirect("/admin/menus");
        });

        MapAccess(admin);
        MapSettings(admin);
    }

    // ---------------------------------------------------------------- users, groups, roles, relations (admin: rw)
    private static void MapAccess(RouteGroupBuilder admin)
    {
        static void RequireAdmin(HttpContext http)
        {
            if (!AdminContext.Get(http).Perms.IsAdmin) throw new AccessDeniedException("This needs admin: rw.");
        }

        admin.MapGet("/users", async (HttpContext http, IDbContextFactory<AppDbContext> dbf, string? error) =>
        {
            RequireAdmin(http);
            await using var db = await dbf.CreateDbContextAsync();
            var users = await db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync();
            var roles = await (from ur in db.UserRoles join r in db.PermissionRoles on ur.RoleId equals r.Id select new { ur.UserId, r.Name }).ToListAsync();
            return Html.Render<UsersPage>(new
            {
                Users = users,
                RolesByUser = roles.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.Select(x => x.Name).Order().ToList()),
                Error = error,
            });
        });

        admin.MapPost("/users", async (HttpContext http, AccountService accounts, [FromForm] string userName, [FromForm] string? displayName,
            [FromForm] string password) =>
        {
            RequireAdmin(http);
            var (result, user) = await accounts.CreateAsync(userName, displayName ?? "", password, AdminContext.Get(http).Actor);
            return result.Ok ? Results.Redirect($"/admin/users/{user!.Id}") : Results.Redirect("/admin/users?error=" + Uri.EscapeDataString(result.Error ?? ""));
        });

        admin.MapGet("/users/{id:int}", async (int id, HttpContext http, IDbContextFactory<AppDbContext> dbf, IPermissionService perms,
            string? message, string? error) =>
        {
            RequireAdmin(http);
            await using var db = await dbf.CreateDbContextAsync();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id) ?? throw new NotFoundException();
            return Html.Render<UserEditPage>(new
            {
                U = user,
                Groups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(),
                Effective = await perms.GetAsync(id),
                Message = message, Error = error,
            });
        });

        admin.MapPost("/users/{id:int}", async (int id, HttpContext http, IDbContextFactory<AppDbContext> dbf, AccountService accounts,
            [FromForm] string? displayName, [FromForm] bool? valid) =>
        {
            RequireAdmin(http);
            var a = AdminContext.Get(http);
            if (id == a.UserId && valid != true) return Results.Redirect($"/admin/users/{id}?error=" + Uri.EscapeDataString("You cannot make your own account invalid."));
            await using (var db = await dbf.CreateDbContextAsync())
            {
                var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id) ?? throw new NotFoundException();
                user.DisplayName = (displayName ?? "").Trim() is { Length: > 0 } d ? d[..Math.Min(d.Length, 200)] : user.UserName ?? "";
                await db.SaveChangesAsync();
            }
            await accounts.SetValidAsync(id, valid == true, a.Actor);
            return Results.Redirect($"/admin/users/{id}?message=Saved.");
        });

        admin.MapPost("/users/{id:int}/password", async (int id, HttpContext http, AccountService accounts, [FromForm] string password) =>
        {
            RequireAdmin(http);
            var r = await accounts.ResetPasswordAsync(id, password, AdminContext.Get(http).Actor);
            return Results.Redirect($"/admin/users/{id}?" + (r.Ok ? "message=Password+changed." : "error=" + Uri.EscapeDataString(r.Error ?? "")));
        });

        foreach (var kind in new[] { "groups", "roles" })
        {
            var roles = kind == "roles";
            admin.MapGet("/" + kind, async (HttpContext http, IDbContextFactory<AppDbContext> dbf, string? error) =>
            {
                RequireAdmin(http);
                await using var db = await dbf.CreateDbContextAsync();
                var rows = roles
                    ? (await db.PermissionRoles.AsNoTracking().OrderBy(r => r.Name).ToListAsync()).Select(r => new GroupsPage.Row(r.Id, r.Name, r.Comment, r.Valid, false)).ToList()
                    : (await db.Groups.AsNoTracking().OrderByDescending(g => g.IsSystem).ThenBy(g => g.Name).ToListAsync()).Select(g => new GroupsPage.Row(g.Id, g.Name, g.Comment, g.Valid, g.IsSystem)).ToList();
                return Html.Render<GroupsPage>(new { Rows = rows, IsRoles = roles, Error = error });
            });
            admin.MapPost("/" + kind, async (HttpContext http, PermissionAdminService svc, [FromForm] int? id, [FromForm] string name,
                [FromForm] string? comment, [FromForm] bool? valid) =>
            {
                var a = AdminContext.Get(http);
                try
                {
                    if (roles) await svc.SaveRoleAsync(a.Perms, id, name, comment ?? "", valid == true, a.Actor);
                    else await svc.SaveGroupAsync(a.Perms, id, name, comment ?? "", valid == true, a.Actor);
                }
                catch (Exception ex) when (ex is BlockException or ConflictException)
                {
                    return Results.Redirect($"/admin/{kind}?error=" + Uri.EscapeDataString(ex.Message));
                }
                return Results.Redirect("/admin/" + kind);
            });
        }

        admin.MapGet("/permissions/{mode}", async Task<IResult> (string mode, HttpContext http, IDbContextFactory<AppDbContext> dbf, int? id) =>
        {
            RequireAdmin(http);
            await using var db = await dbf.CreateDbContextAsync();
            var groups = await db.Groups.AsNoTracking().OrderByDescending(g => g.IsSystem).ThenBy(g => g.Name).ToListAsync();
            switch (mode)
            {
                case "user-groups":
                {
                    var users = await db.Users.AsNoTracking().OrderBy(u => u.UserName).Select(u => new { u.Id, u.UserName }).ToListAsync();
                    var sid = id ?? users.FirstOrDefault()?.Id;
                    HashSet<(int, string)> granted = sid is null ? [] : (await db.UserGroups.AsNoTracking().Where(x => x.UserId == sid).ToListAsync())
                        .Select(x => (x.GroupId, x.Permission)).ToHashSet();
                    return (IResult)Html.Render<GrantMatrixPage>(new
                    {
                        Mode = mode, Subjects = users.Select(u => (u.Id, u.UserName ?? "")).ToList(), SubjectId = sid, Groups = groups, Granted = granted,
                    });
                }
                case "role-groups":
                {
                    var rolesList = await db.PermissionRoles.AsNoTracking().OrderBy(r => r.Name).Select(r => new { r.Id, r.Name }).ToListAsync();
                    var sid = id ?? rolesList.FirstOrDefault()?.Id;
                    HashSet<(int, string)> granted = sid is null ? [] : (await db.RoleGroups.AsNoTracking().Where(x => x.RoleId == sid).ToListAsync())
                        .Select(x => (x.GroupId, x.Permission)).ToHashSet();
                    return (IResult)Html.Render<GrantMatrixPage>(new
                    {
                        Mode = mode, Subjects = rolesList.Select(r => (r.Id, r.Name)).ToList(), SubjectId = sid, Groups = groups, Granted = granted,
                    });
                }
                case "user-roles":
                {
                    var members = (await db.UserRoles.AsNoTracking().ToListAsync()).Select(x => (x.UserId, x.RoleId)).ToHashSet();
                    return (IResult)Html.Render<UserRolesPage>(new
                    {
                        Users = await db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(),
                        Roles = await db.PermissionRoles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(),
                        Members = members,
                    });
                }
                default: throw new NotFoundException();
            }
        });

        admin.MapPost("/permissions/{mode}", async (string mode, HttpContext http, PermissionAdminService svc) =>
        {
            var a = AdminContext.Get(http);
            var form = await http.Request.ReadFormAsync();
            int Int(string k) => int.TryParse(form[k], out var v) ? v : throw new BlockException($"Missing {k}.");
            if (mode == "user-roles")
            {
                await svc.SetUserRoleAsync(a.Perms, Int("userId"), Int("roleId"), form["member"] == "true", a.Actor);
            }
            else
            {
                if (!PermNames.TryParse(form["perm"], out var perm)) throw new BlockException("Unknown permission.");
                var granted = form["granted"] == "true";
                if (mode == "user-groups") await svc.SetUserGroupAsync(a.Perms, Int("subjectId"), Int("groupId"), perm, granted, a.Actor);
                else if (mode == "role-groups") await svc.SetRoleGroupAsync(a.Perms, Int("subjectId"), Int("groupId"), perm, granted, a.Actor);
                else throw new NotFoundException();
            }
            Flash.Ok(http.Response, "Saved.");
            return Results.NoContent();
        });
    }

    private static void MapSettings(RouteGroupBuilder admin)
    {
        admin.MapGet("/settings", async (HttpContext http, IDbContextFactory<AppDbContext> dbf, ServerUpdateService updates, string? message) =>
        {
            if (!AdminContext.Get(http).Perms.IsAdmin) throw new AccessDeniedException("This needs admin: rw.");
            await using var db = await dbf.CreateDbContextAsync();
            return Html.Render<SettingsPage>(new
            {
                S = await SiteSettings.LoadAsync(db),
                Pages = await db.ContentItems.AsNoTracking().Where(c => c.Type == ContentType.Page && c.Status == ContentStatus.Published).OrderBy(c => c.Title).ToListAsync(),
                Groups = await db.Groups.AsNoTracking().Where(g => !g.IsSystem && g.Valid).OrderBy(g => g.Name).ToListAsync(),
                Update = await updates.GetStatusAsync(),
                Message = message,
            });
        });

        admin.MapPost("/settings", async (HttpContext http, IDbContextFactory<AppDbContext> dbf, IOutputCacheStore cache, IAuditLog audit,
            [FromForm] string title, [FromForm] string? tagline, [FromForm] string? baseUrl, [FromForm] string? homePageId, [FromForm] string? defaultGroupId) =>
        {
            var a = AdminContext.Get(http);
            if (!a.Perms.IsAdmin) throw new AccessDeniedException("This needs admin: rw.");
            var url = (baseUrl ?? "").Trim().TrimEnd('/');
            if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
                throw new BlockException("The public address must start with https:// or http://.");
            await using var db = await dbf.CreateDbContextAsync();
            await SiteSettings.SetAsync(db, SiteSettings.TitleKey, Clip(title, 200));
            await SiteSettings.SetAsync(db, SiteSettings.TaglineKey, Clip(tagline ?? "", 300));
            await SiteSettings.SetAsync(db, SiteSettings.BaseUrlKey, url);
            await SiteSettings.SetAsync(db, SiteSettings.HomePageKey, int.TryParse(homePageId, out var h) ? h.ToString() : "");
            if (int.TryParse(defaultGroupId, out var g)) await SiteSettings.SetAsync(db, SiteSettings.DefaultGroupKey, g.ToString());
            await db.SaveChangesAsync();
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            await audit.WriteAsync(a.Actor, "settings.save", "Settings", "site", title);
            return Results.Redirect("/admin/settings?message=Saved.");
        });

        admin.MapPost("/updates/check", async (HttpContext http, ServerUpdateService updates) =>
        {
            var a = AdminContext.Get(http);
            if (!a.Perms.IsAdmin) throw new AccessDeniedException("This needs admin: rw.");
            await updates.CheckNowAsync(a.Actor);
            return Results.Redirect("/admin/settings");
        });

        admin.MapPost("/updates/apply", async (HttpContext http, ServerUpdateService updates) =>
        {
            var a = AdminContext.Get(http);
            if (!a.Perms.IsAdmin) throw new AccessDeniedException("This needs admin: rw.");
            var r = await updates.UpdateNowAsync(a.Actor);
            if (!r.Ok) throw new BlockException(r.Error ?? "The update could not be started.");
            return Results.Redirect("/admin/settings?message=" + Uri.EscapeDataString("Update staged. The site restarts in a moment."));
        });

        admin.MapGet("/audit", async (HttpContext http, IDbContextFactory<AppDbContext> dbf) =>
        {
            if (!AdminContext.Get(http).Perms.HasSystem(SystemGroups.Audit, Perm.Ro)) throw new AccessDeniedException("The audit log needs audit: ro.");
            await using var db = await dbf.CreateDbContextAsync();
            return Html.Render<AuditPage>(new { Entries = await db.AuditLog.AsNoTracking().OrderByDescending(x => x.Id).Take(500).ToListAsync() });
        });
    }

    // ---------------------------------------------------------------- helpers
    private static async Task<IResult> ContentSettings(int id, HttpContext http, ContentService content, IDbContextFactory<AppDbContext> dbf, string? error)
    {
        var a = AdminContext.Get(http);
        var item = await content.GetForViewAsync(a.Perms, id);
        var facts = ContentService.Facts(item);
        await using var db = await dbf.CreateDbContextAsync();
        var groups = await db.Groups.AsNoTracking().Where(g => g.Valid && !g.IsSystem).OrderBy(g => g.Name).ToListAsync();
        return Html.Render<ContentSettingsPage>(new
        {
            Item = item,
            MoveTargets = groups.Where(g => g.Id == item.GroupId || ContentRules.CanMove(a.Perms, facts, g.Id)).ToList(),
            Revisions = await db.Revisions.AsNoTracking().Where(r => r.ContentItemId == id).OrderByDescending(r => r.Id).Take(20).ToListAsync(),
            CanEdit = ContentRules.CanEdit(a.Perms, facts),
            CanPublish = ContentRules.CanPublish(a.Perms, facts),
            CanDelete = ContentRules.CanDelete(a.Perms, facts),
            Error = error,
        }, error is null ? 200 : 400);
    }

    private static List<Group> CreatableGroups(EffectivePermissions p, List<Group> groups) =>
        groups.Where(g => g.Valid && !g.IsSystem && p.Has(g.Id, Perm.Create)).OrderBy(g => g.Name).ToList();

    private static EditorTarget ParseTarget(string s) =>
        EditorTarget.TryParse(s, out var t) ? t.Value : throw new NotFoundException();

    private static string? Do(Action a) { a(); return null; }

    private static string Clip(string s, int max) { s = s.Trim(); return s.Length <= max ? s : s[..max]; }

    private static double Num(Microsoft.Extensions.Primitives.StringValues v, double fallback) =>
        double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;
}

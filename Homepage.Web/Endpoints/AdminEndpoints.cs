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
            SignInLimiter limiter, TimeProvider time, [FromForm] string userName, [FromForm] string password, [FromForm] string? returnUrl) =>
        {
            if (!await af.IsRequestValidAsync(http)) return Results.BadRequest();
            const string fail = "Wrong user name or password, or too many attempts. Wait a few minutes and try again.";
            var ip = http.Connection.RemoteIpAddress;
            var ipText = AuditRequestInfo.Normalize(ip);
            IResult Failed(string? name = null) =>
                Html.Render<LoginPage>(new { Token = af.GetAndStoreTokens(http).RequestToken ?? "", Error = fail, UserName = name }, 401);

            var reservation = limiter.TryReserve((userName ?? "").Trim().ToUpperInvariant(), ip);
            if (reservation is null)
            {
                await audit.WriteAsync("(sign-in)", "signin.limited", "User", "", "", ipText);
                return Failed(userName);
            }
            var user = await users.FindByNameAsync((userName ?? "").Trim());
            // Always pay for one password hash, so the response time does not tell which user names exist.
            var ok = user is not null ? await users.CheckPasswordAsync(user, password ?? "") : DummyPasswordCheck(password ?? "");
            if (user is null || !user.Valid || !ok)
            {
                // Never store the typed name of an unknown account: it is often a password typed into the wrong field.
                await audit.WriteAsync(user?.UserName ?? "(unknown user)", "signin.failed", "User", user?.Id.ToString() ?? "", "", ipText);
                return Failed(userName);
            }
            limiter.Succeeded(reservation);
            if (!(await perms.GetAsync(user.Id)).CanSeeAdmin)
                return Html.Render<LoginPage>(new { Token = af.GetAndStoreTokens(http).RequestToken ?? "", Error = "This account has no permissions yet. Ask an administrator.", UserName = userName }, 403);
            if (user.TwoFactorEnabled)
            {
                // Password checked: the code from the authenticator app comes next (5 minutes, bound to this account's stamp).
                AdminSession.SetPending(http, user, returnUrl);
                return Results.Redirect("/admin/login/2fa");
            }
            await AdminSession.SignInAsync(http, user, ipText, time.GetUtcNow());
            await audit.WriteAsync(user.UserName ?? "", "signin", "User", user.Id.ToString(), "", ipText);
            return Results.Redirect(AdminSession.SafeReturnUrl(returnUrl));
        }).RequireRateLimiting("signin");

        anon.MapGet("/login/2fa", (HttpContext http, IAntiforgery af) =>
            AdminSession.GetPending(http) is null
                ? Results.Redirect("/admin/login")
                : (IResult)Html.Render<LoginTwoFactorPage>(new { Token = af.GetAndStoreTokens(http).RequestToken ?? "" }));

        anon.MapPost("/login/2fa", async (HttpContext http, IAntiforgery af, UserManager<User> users, IAuditLog audit, SignInLimiter limiter,
            TimeProvider time, [FromForm] string code) =>
        {
            if (!await af.IsRequestValidAsync(http)) return Results.BadRequest();
            var pending = AdminSession.GetPending(http);
            if (pending is null) return Results.Redirect("/admin/login");
            var ip = http.Connection.RemoteIpAddress;
            var ipText = AuditRequestInfo.Normalize(ip);
            IResult Failed(string message) =>
                Html.Render<LoginTwoFactorPage>(new { Token = af.GetAndStoreTokens(http).RequestToken ?? "", Error = message }, 401);
            var user = await users.FindByIdAsync(pending.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (user is null || !user.Valid || !user.TwoFactorEnabled || user.SecurityStamp != pending.Stamp)
            {
                AdminSession.ClearPending(http);
                return Results.Redirect("/admin/login");
            }
            var reservation = limiter.TryReserve("2FA|" + user.Id, ip);
            if (reservation is null) return Failed("Too many attempts. Wait a few minutes and try again.");
            var clean = new string((code ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
            var ok = clean.Length == 6 && clean.All(char.IsAsciiDigit)
                ? await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, clean)
                : clean.Length > 0 && (await users.RedeemTwoFactorRecoveryCodeAsync(user, (code ?? "").Trim())).Succeeded;
            if (!ok)
            {
                await audit.WriteAsync(user.UserName ?? "", "signin.failed", "User", user.Id.ToString(), "wrong two-factor code", ipText);
                return Failed("That code is not right. Use the current code from your authenticator app, or a recovery code.");
            }
            limiter.Succeeded(reservation);
            AdminSession.ClearPending(http);
            user = await users.FindByIdAsync(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)); // a used recovery code changes the record
            await AdminSession.SignInAsync(http, user!, ipText, time.GetUtcNow());
            await audit.WriteAsync(user!.UserName ?? "", "signin", "User", user.Id.ToString(), clean.Length == 6 ? "two-factor" : "two-factor, recovery code", ipText);
            return Results.Redirect(AdminSession.SafeReturnUrl(pending.ReturnUrl));
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

        admin.MapGet("", async (HttpContext http, ContentService content, ThemeService themes, BlogService blogs, IDbContextFactory<AppDbContext> dbf,
            Homepage.Analytics.AnalyticsQueries analytics, Homepage.Analytics.AnalyticsCollector collector, TimeProvider time) =>
        {
            var a = AdminContext.Get(http);
            long? visitorsToday = null;
            if (a.Perms.HasSystem(SystemGroups.Analytics, Perm.Ro))
            {
                var tz = (await collector.SettingsAsync()).Zone;
                visitorsToday = (await analytics.SummaryAsync(Homepage.Analytics.AnalyticsRange.Resolve("today", null, null, tz, time.GetUtcNow().UtcDateTime),
                    new Dictionary<string, string>())).Visitors;
            }
            var (pages, posts, drafts) = await content.CountsAsync(a.Perms);
            var recent = await content.ListAsync(a.Perms, new ContentQuery(PageSize: 10));
            await using var db = await dbf.CreateDbContextAsync();
            var groups = await db.Groups.AsNoTracking().ToListAsync();
            var site = await SiteSettings.LoadAsync(db);
            return Html.Render<DashboardPage>(new
            {
                Pages = pages, Posts = posts, Drafts = drafts, VisitorsToday = visitorsToday,
                ThemeName = (await themes.GetActiveAsync()).Name,
                Recent = recent.Items,
                GroupNames = groups.Where(g => a.Perms.Has(g.Id, Perm.Ro)).ToDictionary(g => g.Id, g => g.Name),
                CreateGroups = CreatableGroups(a.Perms, groups),
                DefaultGroupId = site.DefaultGroupId,
                Blogs = await blogs.AllAsync(),
                HomePageId = site.HomePageId,
            });
        });

        // ---------------------------------------------------------------- content
        admin.MapGet("/content", async (HttpContext http, HttpRequest req, ContentService content, BlogService blogs, IDbContextFactory<AppDbContext> dbf) =>
        {
            var a = AdminContext.Get(http);
            var url = ListUrl.From(req, "type", "q", "group", "blog", "status", "sort", "size", "page");
            var t = Enum.TryParse<ContentType>(url["type"], ignoreCase: true, out var parsed) ? parsed : ContentType.Page;
            var result = await content.ListAsync(a.Perms, new ContentQuery(t, Int(url["group"]), Int(url["blog"]), url["status"], url["q"], url["sort"],
                Int(url["page"]), Int(url["size"])));
            await using var db = await dbf.CreateDbContextAsync();
            var groups = await db.Groups.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
            var visible = a.Perms.GroupsWith(Perm.Ro).ToHashSet();
            var site = await SiteSettings.LoadAsync(db);
            return Html.Render<ContentListPage>(new
            {
                Type = t, Result = result, Url = url,
                Groups = groups.Where(x => visible.Contains(x.Id) && !x.IsSystem).ToDictionary(x => x.Id, x => x.Name),
                CreateGroups = CreatableGroups(a.Perms, groups),
                Blogs = await blogs.AllAsync(),
                DefaultGroupId = site.DefaultGroupId,
                HomePageId = site.HomePageId,
            });
        });

        admin.MapPost("/content/new", async (HttpContext http, ContentService content,
            [FromForm] string title, [FromForm] string type, [FromForm] int groupId, [FromForm] int? blogId) =>
        {
            var a = AdminContext.Get(http);
            var t = Enum.TryParse<ContentType>(type, ignoreCase: true, out var parsed) ? parsed : throw new BlockException("Unknown content type.");
            var item = await content.CreateAsync(a.Perms, t, title, groupId, a.Actor, t == ContentType.Post ? blogId : null);
            return Results.Redirect($"/admin/editor/content-{item.Id}");
        });

        admin.MapGet("/content/{id:int}", async (int id, HttpContext http, ContentService content, IDbContextFactory<AppDbContext> dbf, BlogService blogs) =>
            await ContentSettings(id, http, content, dbf, blogs, null));

        admin.MapPost("/content/{id:int}", async (int id, HttpContext http, ContentService content, IDbContextFactory<AppDbContext> dbf,
            IOutputCacheStore cache, BlogService blogs, [FromForm] string title, [FromForm] string slug, [FromForm] string? excerpt, [FromForm] int groupId,
            [FromForm] int? blogId) =>
        {
            var a = AdminContext.Get(http);
            try
            {
                var r = await content.UpdateSettingsAsync(a.Perms, id, title, slug, excerpt ?? "", groupId, a.Actor, blogId);
                if (r.AffectsSite) await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            }
            catch (Exception ex) when (ex is BlockException or ConflictException)
            {
                return await ContentSettings(id, http, content, dbf, blogs, ex.Message);
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

        admin.MapPost("/content/{id:int}/home", async (int id, HttpContext http, ContentService content, IDbContextFactory<AppDbContext> dbf,
            IOutputCacheStore cache, IAuditLog audit) =>
        {
            var a = AdminContext.Get(http);
            if (!a.Perms.IsAdmin) throw new AccessDeniedException("Choosing the home page needs admin: rw.");
            var item = await content.GetForViewAsync(a.Perms, id);
            if (item.Type != ContentType.Page || item.Status != ContentStatus.Published) throw new BlockException("Only a published page can be the home page.");
            await using var db = await dbf.CreateDbContextAsync();
            await SiteSettings.SetAsync(db, SiteSettings.HomePageKey, id.ToString());
            await db.SaveChangesAsync();
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            await audit.WriteAsync(a.Actor, "settings.home", "Page", id.ToString(), item.Title);
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
                theme = ThemeService.ToActiveDraft(await db.Themes.AsNoTracking().FirstAsync(t => t.Id == doc.ThemeId));
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
                theme = ThemeService.ToActiveDraft(await db.Themes.AsNoTracking().FirstAsync(t => t.Id == doc.ThemeId));
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
            MediaService media, MenuService menus, BlogService blogs) =>
        {
            var a = AdminContext.Get(http);
            var doc = await content.LoadDocumentAsync(a.Perms, ParseTarget(target));
            var node = doc.Tree.Find(blockId) ?? throw new NotFoundException("The block no longer exists.");
            var def = BlockRegistry.Get(node.Type);
            var canEdit = doc.CanEdit && (def?.RequiredSystemGroup is not { } g || a.Perms.HasSystem(g, Perm.Rw));
            return Html.Render<Inspector>(new
            {
                Node = node, Target = doc.Target, CanEdit = canEdit,
                Media = await media.ListAsync(a.Perms), Menus = await menus.ListAsync(), Blogs = await blogs.AllAsync(),
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
        admin.MapGet("/media", async (HttpContext http, HttpRequest req, MediaService media, IDbContextFactory<AppDbContext> dbf) =>
        {
            var a = AdminContext.Get(http);
            var url = ListUrl.From(req, "q", "group", "kind", "size", "page");
            await using var db = await dbf.CreateDbContextAsync();
            var groups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
            var visible = a.Perms.GroupsWith(Perm.Ro).ToHashSet();
            return Html.Render<MediaPage>(new
            {
                Result = await media.QueryAsync(a.Perms, url["q"], Int(url["group"]), url["kind"], Int(url["page"]), Int(url["size"])),
                Url = url,
                UploadGroups = CreatableGroups(a.Perms, groups),
                Groups = groups.Where(g => visible.Contains(g.Id) && !g.IsSystem).ToDictionary(g => g.Id, g => g.Name),
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

        admin.MapPost("/media/{id:int}/delete", async (int id, HttpContext http, MediaService media, PublicContent pub, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            await media.DeleteAsync(a.Perms, id, a.Actor);
            pub.ForgetMedia(id);
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default); // pages, logo or favicon may have shown it
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
            var (message, error) = FlashCookie.Take(http);
            return Html.Render<ThemeEditPage>(new { Theme = theme, Message = message, Error = error,
                CanEditLayout = a.Perms.HasSystem(SystemGroups.Layout, Perm.Rw) });
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
            await themes.SaveDraftAsync(a.Perms, id, form["name"].ToString(), tokens, form["customCss"].ToString());
            if (http.Request.Headers.ContainsKey("HX-Request"))
            {
                Flash.Ok(http.Response, "Draft saved. Publish to make it live.");
                http.Response.Headers["HX-Refresh"] = "true"; // updates the draft status and buttons
                return Results.NoContent();
            }
            return Results.Redirect($"/admin/themes/{id}");
        });

        admin.MapPost("/themes/{id:int}/publish", async (int id, HttpContext http, ThemeService themes, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            var live = await themes.PublishAsync(a.Perms, id, a.Actor);
            if (live) await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            if (http.Request.Headers.ContainsKey("HX-Request"))
            {
                Flash.Ok(http.Response, live ? "Published. The site uses the new version." : "Published.");
                http.Response.Headers["HX-Refresh"] = "true";
                return Results.NoContent();
            }
            return FlashCookie.Redirect(http, $"/admin/themes/{id}", live ? "Published. The site uses the new version." : "Published.");
        });

        admin.MapPost("/themes/{id:int}/discard", async (int id, HttpContext http, ThemeService themes) =>
        {
            var a = AdminContext.Get(http);
            await themes.DiscardDraftAsync(a.Perms, id, a.Actor);
            return FlashCookie.Redirect(http, $"/admin/themes/{id}", "Unpublished changes discarded.");
        });

        admin.MapGet("/themes/{id:int}/preview", async (int id, HttpContext http, ThemeService themes, RenderContextFactory ctxf, PublicContent pub) =>
        {
            var a = AdminContext.Get(http);
            var theme = await themes.GetAsync(a.Perms, id);
            var active = ThemeService.ToActiveDraft(theme); // the preview shows unpublished changes
            var ctx = await ctxf.CreateAsync(theme: active);
            var home = ctx.Site.HomePageId is { } hid ? await pub.GetPublishedByIdAsync(hid) : null;
            return Html.Render<ThemePreview>(new
            {
                Ctx = ctx,
                Tree = home is null ? new BlockTree() : BlockTree.Parse(home.Blocks),
                Css = active.Tokens.ToCss(theme.DraftCustomCss ?? theme.CustomCss),
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
        static void RequireLayout(HttpContext http, string what)
        {
            if (!AdminContext.Get(http).Perms.HasSystem(SystemGroups.Layout, Perm.Rw)) throw new AccessDeniedException($"{what} need layout: rw.");
        }
        static List<MenuLink> FormLinks(IFormCollection form)
        {
            var labels = form["label"];
            var urls = form["url"];
            return labels.Select((l, i) => new MenuLink(l ?? "", i < urls.Count ? urls[i] ?? "" : "")).ToList();
        }

        admin.MapGet("/menus", async (HttpContext http, MenuService menus) =>
        {
            RequireLayout(http, "Menus");
            var (message, error) = FlashCookie.Take(http);
            return Html.Render<MenusPage>(new { Menus = await menus.ListAsync(), Message = message, Error = error });
        });

        admin.MapGet("/menus/new", (HttpContext http) =>
        {
            RequireLayout(http, "Menus");
            return Html.Render<MenuEditPage>(new { });
        });

        admin.MapGet("/menus/{key}", async (string key, HttpContext http, MenuService menus) =>
        {
            RequireLayout(http, "Menus");
            var menu = (await menus.ListAsync()).FirstOrDefault(m => m.Key == key) ?? throw new NotFoundException();
            var (message, error) = FlashCookie.Take(http);
            return Html.Render<MenuEditPage>(new { Menu = menu, menu.Name, menu.Key, Links = MenuService.Parse(menu.Items), Message = message, Error = error });
        });

        admin.MapPost("/menus", async (HttpContext http, MenuService menus, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            var form = await http.Request.ReadFormAsync();
            var links = FormLinks(form);
            string name = form["name"].ToString(), key = form["key"].ToString();
            try
            {
                if (links.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Label) && !SafeUrlAttribute.IsSafe(l.Url)) is { } bad)
                    throw new BlockException($"'{bad.Url}' is not an allowed link.");
                var menu = await menus.CreateAsync(a.Perms, name, key, a.Actor);
                if (links.Any(l => !string.IsNullOrWhiteSpace(l.Label))) await menus.SaveAsync(a.Perms, menu.Key, links, a.Actor);
                await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
                return FlashCookie.Redirect(http, $"/admin/menus/{menu.Key}", "Menu created. Show it with a Menu block.");
            }
            catch (Exception ex) when (ex is BlockException or ConflictException)
            {
                // keep what was typed
                return Html.Render<MenuEditPage>(new { Name = name, Key = key, Links = links, Error = ex.Message });
            }
        });

        admin.MapPost("/menus/{key}/delete", async (string key, HttpContext http, MenuService menus, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            await menus.DeleteAsync(a.Perms, key, a.Actor);
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            return FlashCookie.Redirect(http, "/admin/menus", "Menu deleted.");
        });

        admin.MapPost("/menus/{key}", async (string key, HttpContext http, MenuService menus, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            var form = await http.Request.ReadFormAsync();
            var links = FormLinks(form);
            try { await menus.SaveAsync(a.Perms, key, links, a.Actor, name: form["name"].ToString()); }
            catch (BlockException ex)
            {
                var menu = (await menus.ListAsync()).FirstOrDefault(m => m.Key == key) ?? throw new NotFoundException();
                return Html.Render<MenuEditPage>(new { Menu = menu, Name = form["name"].ToString(), menu.Key, Links = links, Error = ex.Message });
            }
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            return FlashCookie.Redirect(http, $"/admin/menus/{key}", "Saved.");
        });

        // ---------------------------------------------------------------- blogs (layout: rw)
        static async Task<Dictionary<int, int>> PostCounts(HttpContext http, AppDbContext db)
        {
            var visible = AdminContext.Get(http).Perms.GroupsWith(Perm.Ro);
            return await db.ContentItems.Where(c => c.BlogId != null && visible.Contains(c.GroupId)).GroupBy(c => c.BlogId!.Value)
                .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        }
        static async Task<IResult> BlogEditor(HttpContext http, BlogService blogs, AppDbContext db, Blog? blog, string? message, string? error,
            string? name = null, string? slug = null, string? description = null, int? postsPerPage = null)
        {
            var all = await blogs.AllAsync();
            // Deleting needs no posts at all (not just none visible to this user); the service checks again.
            var count = blog is null ? 0 : await db.ContentItems.CountAsync(c => c.BlogId == blog.Id);
            return Html.Render<BlogEditPage>(new
            {
                Blog = blog,
                Name = name ?? blog?.Name ?? "", Slug = slug ?? blog?.Slug ?? "", Description = description ?? blog?.Description ?? "",
                PostsPerPage = postsPerPage ?? blog?.PostsPerPage ?? 10,
                PostCount = blog is null ? 0 : (await PostCounts(http, db)).GetValueOrDefault(blog.Id),
                CanDelete = blog is not null && count == 0 && all.Count > 1,
                HomeBlogId = (await SiteSettings.LoadAsync(db)).HomeBlogId,
                Message = message, Error = error,
            });
        }

        admin.MapGet("/blogs", async (HttpContext http, BlogService blogs, IDbContextFactory<AppDbContext> dbf) =>
        {
            RequireLayout(http, "Blogs");
            var (message, error) = FlashCookie.Take(http);
            await using var db = await dbf.CreateDbContextAsync();
            return Html.Render<BlogsPage>(new { Blogs = await blogs.AllAsync(), PostCounts = await PostCounts(http, db), HomeBlogId = (await SiteSettings.LoadAsync(db)).HomeBlogId, Message = message, Error = error });
        });

        admin.MapGet("/blogs/new", async (HttpContext http, BlogService blogs, IDbContextFactory<AppDbContext> dbf) =>
        {
            RequireLayout(http, "Blogs");
            await using var db = await dbf.CreateDbContextAsync();
            return await BlogEditor(http, blogs, db, null, null, null);
        });

        admin.MapGet("/blogs/{id:int}", async (int id, HttpContext http, BlogService blogs, IDbContextFactory<AppDbContext> dbf) =>
        {
            RequireLayout(http, "Blogs");
            var blog = await blogs.ByIdAsync(id) ?? throw new NotFoundException();
            var (message, error) = FlashCookie.Take(http);
            await using var db = await dbf.CreateDbContextAsync();
            return await BlogEditor(http, blogs, db, blog, message, error);
        });

        admin.MapPost("/blogs", async (HttpContext http, BlogService blogs, IOutputCacheStore cache, IDbContextFactory<AppDbContext> dbf,
            [FromForm] int? id, [FromForm] string name, [FromForm] string slug, [FromForm] string? description, [FromForm] int? postsPerPage) =>
        {
            var a = AdminContext.Get(http);
            Blog saved;
            try { saved = await blogs.SaveAsync(a.Perms, id, name, slug, description ?? "", postsPerPage ?? 10, a.Actor); }
            catch (Exception ex) when (ex is BlockException or ConflictException)
            {
                await using var db = await dbf.CreateDbContextAsync();
                var blog = id is { } bid ? await blogs.ByIdAsync(bid) ?? throw new NotFoundException() : null;
                return await BlogEditor(http, blogs, db, blog, null, ex.Message, name, slug, description, postsPerPage);
            }
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            return FlashCookie.Redirect(http, $"/admin/blogs/{saved.Id}", id is null ? "Blog created. Pick it when you write a post." : "Saved.");
        });

        admin.MapPost("/blogs/{id:int}/delete", async (int id, HttpContext http, BlogService blogs, IOutputCacheStore cache) =>
        {
            var a = AdminContext.Get(http);
            try { await blogs.DeleteAsync(a.Perms, id, a.Actor); }
            catch (ConflictException ex) { return FlashCookie.Redirect(http, $"/admin/blogs/{id}", ex.Message, error: true); }
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            return FlashCookie.Redirect(http, "/admin/blogs", "Blog deleted.");
        });

        MapAnalytics(admin);
        MapProfile(admin);
        MapAccess(admin);
        MapSettings(admin);
    }

    // ---------------------------------------------------------------- analytics (analytics: ro to view, rw for settings)
    private static void MapAnalytics(RouteGroupBuilder admin)
    {
        static void RequireAnalytics(HttpContext http, Perm perm)
        {
            if (!AdminContext.Get(http).Perms.HasSystem(SystemGroups.Analytics, perm))
                throw new AccessDeniedException(perm == Perm.Ro ? "Analytics needs analytics: ro." : "Changing analytics needs analytics: rw.");
        }

        admin.MapGet("/analytics", async (HttpContext http, HttpRequest req, Homepage.Analytics.AnalyticsQueries queries,
            Homepage.Analytics.AnalyticsCollector collector, Homepage.Analytics.CountryLookup geo, TimeProvider time, CancellationToken ct) =>
        {
            RequireAnalytics(http, Perm.Ro);
            var (message, error) = FlashCookie.Take(http);
            var settings = await collector.SettingsAsync(ct);
            var url = AnalyticsUrl.Parse(req);
            var now = time.GetUtcNow().UtcDateTime;
            var range = Homepage.Analytics.AnalyticsRange.Resolve(url.RangeKey, url.From, url.To, settings.Zone, now);
            var f = url.Filters;
            var breakdowns = new Dictionary<string, List<Homepage.Analytics.AnalyticsRow>>();
            foreach (var dim in new[] { "page", "entry", "source", "channel", "utm_campaign", "country", "device", "browser", "os" })
                breakdowns[dim] = await queries.BreakdownAsync(dim, range, f, 10, ct);
            return Html.Render<AnalyticsPage>(new
            {
                Range = range, Url = url,
                Summary = await queries.SummaryAsync(range, f, ct),
                Previous = await queries.SummaryAsync(range.Previous(settings.Zone, now), f, ct),
                Points = await queries.TimeseriesAsync(range, f, settings.TimeZone, ct),
                Breakdowns = breakdowns,
                RealtimeVisitors = (await queries.RealtimeAsync(now, ct)).Visitors,
                Settings = settings,
                CanManage = AdminContext.Get(http).Perms.HasSystem(SystemGroups.Analytics, Perm.Rw),
                StoredTotal = await queries.CountAllAsync(ct),
                GeoDate = geo.DatabaseDateUtc, OwnGeoFile = geo.UsesOwnFile,
                TimeZones = TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).Where(id => id.Contains('/') || id == "UTC").Order().ToList(),
                Message = message, Error = error,
            });
        });

        admin.MapGet("/analytics/realtime", async (HttpContext http, Homepage.Analytics.AnalyticsQueries queries, TimeProvider time, CancellationToken ct) =>
        {
            RequireAnalytics(http, Perm.Ro);
            return Html.Render<AnalyticsRealtime>(new { Visitors = (await queries.RealtimeAsync(time.GetUtcNow().UtcDateTime, ct)).Visitors });
        });

        admin.MapGet("/analytics/export", async (HttpContext http, HttpRequest req, string dim, Homepage.Analytics.AnalyticsQueries queries,
            Homepage.Analytics.AnalyticsCollector collector, TimeProvider time, CancellationToken ct) =>
        {
            RequireAnalytics(http, Perm.Ro);
            if (!Homepage.Analytics.AnalyticsQueries.Dimensions.ContainsKey(dim)) return Results.BadRequest();
            var settings = await collector.SettingsAsync(ct);
            var url = AnalyticsUrl.Parse(req);
            var range = Homepage.Analytics.AnalyticsRange.Resolve(url.RangeKey, url.From, url.To, settings.Zone, time.GetUtcNow().UtcDateTime);
            var rows = await queries.BreakdownAsync(dim, range, url.Filters, 1000, ct);
            var sb = new System.Text.StringBuilder($"{dim},visitors,pageviews\n");
            foreach (var r in rows) sb.Append(Csv(AnalyticsFormat.Label(dim, r.Value))).Append(',').Append(r.Visitors).Append(',').Append(r.Pageviews).Append('\n');
            return Results.File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8",
                $"analytics-{dim}-{range.FromLocal:yyyy-MM-dd}-{range.ToLocal.AddDays(-1):yyyy-MM-dd}.csv");
        });

        admin.MapPost("/analytics/settings", async (HttpContext http, IDbContextFactory<AppDbContext> dbf, Homepage.Analytics.AnalyticsCollector collector,
            IOutputCacheStore cache, IAuditLog audit, [FromForm] bool? enabled, [FromForm] bool? countries, [FromForm] int? retentionDays,
            [FromForm] string? timeZone) =>
        {
            RequireAnalytics(http, Perm.Rw);
            var tz = (timeZone ?? "").Trim();
            if (Homepage.Analytics.AnalyticsSettings.TryTimeZone(tz) is null)
                return FlashCookie.Redirect(http, "/admin/analytics", $"'{tz}' is not a time zone this server knows (use an IANA name such as Europe/Warsaw).", error: true);
            var days = Math.Clamp(retentionDays ?? 730, Homepage.Analytics.AnalyticsSettings.MinRetentionDays, Homepage.Analytics.AnalyticsSettings.MaxRetentionDays);
            await using (var db = await dbf.CreateDbContextAsync())
            {
                try { await db.Database.SqlQuery<DateTime>($"SELECT (now() AT TIME ZONE {tz}) AS \"Value\"").ToListAsync(); }
                catch (Npgsql.PostgresException) { return FlashCookie.Redirect(http, "/admin/analytics", $"PostgreSQL does not know the time zone '{tz}'.", error: true); }
                await SiteSettings.SetAsync(db, Homepage.Analytics.AnalyticsSettings.EnabledKey, enabled == true ? "true" : "false");
                await SiteSettings.SetAsync(db, Homepage.Analytics.AnalyticsSettings.CountriesKey, countries == true ? "true" : "false");
                await SiteSettings.SetAsync(db, Homepage.Analytics.AnalyticsSettings.RetentionKey, days.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await SiteSettings.SetAsync(db, Homepage.Analytics.AnalyticsSettings.TimeZoneKey, tz);
                await db.SaveChangesAsync();
            }
            collector.ForgetSettings();
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default); // pages include the script only when counting is on
            await audit.WriteAsync(AdminContext.Get(http).Actor, "analytics.settings", "Settings", "analytics",
                $"counting {(enabled == true ? "on" : "off")}, countries {(countries == true ? "on" : "off")}, keep {days} days, {tz}");
            return FlashCookie.Redirect(http, "/admin/analytics", "Saved.");
        });

        admin.MapPost("/analytics/delete", async (HttpContext http, Homepage.Analytics.AnalyticsQueries queries, IAuditLog audit) =>
        {
            RequireAnalytics(http, Perm.Rw);
            var n = await queries.CountAllAsync();
            await queries.DeleteAllAsync();
            await audit.WriteAsync(AdminContext.Get(http).Actor, "analytics.delete", "Settings", "analytics", $"{n} page views");
            return FlashCookie.Redirect(http, "/admin/analytics", "All analytics data was deleted.");
        });
    }

    private static string Csv(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 || s.StartsWith('=') || s.StartsWith('+') || s.StartsWith('-') || s.StartsWith('@')
            ? "\"" + (s.StartsWith('=') || s.StartsWith('+') || s.StartsWith('-') || s.StartsWith('@') ? "'" : "") + s.Replace("\"", "\"\"") + "\""
            : s;

    // ---------------------------------------------------------------- the signed-in user's own profile
    private static void MapProfile(RouteGroupBuilder admin)
    {
        static async Task<User> Me(HttpContext http, UserManager<User> users) =>
            await users.FindByIdAsync(AdminContext.Get(http).UserId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ?? throw new NotFoundException();

        admin.MapGet("/profile", async (HttpContext http, UserManager<User> users) =>
        {
            var me = await Me(http, users);
            var (message, error) = FlashCookie.Take(http);
            return Html.Render<ProfilePage>(new { U = me, RecoveryCodesLeft = await users.CountRecoveryCodesAsync(me), Message = message, Error = error });
        });

        admin.MapPost("/profile", async (HttpContext http, UserManager<User> users, IAuditLog audit, PublicContent content, IOutputCacheStore cache,
            [FromForm] string displayName, [FromForm] string? email) =>
        {
            var me = await Me(http, users);
            displayName = (displayName ?? "").Trim();
            if (displayName.Length is 0 or > 200) return FlashCookie.Redirect(http, "/admin/profile", "The display name must be 1 to 200 characters.", error: true);
            var clean = Homepage.Accounts.AccountService.CleanEmail(email, out var emailOk);
            if (!emailOk) return FlashCookie.Redirect(http, "/admin/profile", "That email address does not look right.", error: true);
            me.DisplayName = displayName;
            var r = await users.UpdateAsync(me);
            if (r.Succeeded && me.Email != clean) r = await users.SetEmailAsync(me, clean);
            if (!r.Succeeded) return FlashCookie.Redirect(http, "/admin/profile", string.Join(" ", r.Errors.Select(e => e.Description)), error: true);
            await AdminSession.RefreshAsync(http, me);
            content.ForgetAuthor(me.Id);
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default); // posts show the display name
            await audit.WriteAsync(me.UserName ?? "", "user.profile", "User", me.Id.ToString(), "display name and email");
            return FlashCookie.Redirect(http, "/admin/profile", "Saved.");
        });

        admin.MapPost("/profile/password", async (HttpContext http, UserManager<User> users, IAuditLog audit,
            [FromForm] string current, [FromForm] string password, [FromForm] string confirm) =>
        {
            var me = await Me(http, users);
            if (password != confirm) return FlashCookie.Redirect(http, "/admin/profile", "The new passwords do not match.", error: true);
            if ((password ?? "").Length < Homepage.Accounts.AccountService.MinPasswordLength)
                return FlashCookie.Redirect(http, "/admin/profile", $"Passwords need at least {Homepage.Accounts.AccountService.MinPasswordLength} characters.", error: true);
            var r = await users.ChangePasswordAsync(me, current ?? "", password!);
            if (!r.Succeeded)
                return FlashCookie.Redirect(http, "/admin/profile", r.Errors.Any(e => e.Code == "PasswordMismatch") ? "The current password is not right." : string.Join(" ", r.Errors.Select(e => e.Description)), error: true);
            await AdminSession.RefreshAsync(http, me); // other sessions end (new security stamp); this one continues
            await audit.WriteAsync(me.UserName ?? "", "user.password", "User", me.Id.ToString(), "changed own password");
            return FlashCookie.Redirect(http, "/admin/profile", "Password changed. You were signed out everywhere else.");
        });

        admin.MapGet("/profile/2fa", async (HttpContext http, UserManager<User> users, IDbContextFactory<AppDbContext> dbf) =>
        {
            var me = await Me(http, users);
            if (me.TwoFactorEnabled) return Results.Redirect("/admin/profile");
            var key = await users.GetAuthenticatorKeyAsync(me);
            if (string.IsNullOrEmpty(key))
            {
                await users.ResetAuthenticatorKeyAsync(me);
                key = await users.GetAuthenticatorKeyAsync(me);
                await AdminSession.RefreshAsync(http, me);
            }
            await using var db = await dbf.CreateDbContextAsync();
            var issuer = (await SiteSettings.LoadAsync(db)).Title;
            var label = Uri.EscapeDataString($"{issuer}:{me.UserName}");
            var uri = $"otpauth://totp/{label}?secret={key}&issuer={Uri.EscapeDataString(issuer)}&digits=6";
            using var qr = new QRCoder.QRCodeGenerator().CreateQrCode(uri, QRCoder.QRCodeGenerator.ECCLevel.M);
            var svg = new QRCoder.SvgQRCode(qr).GetGraphic(5);
            var (_, error) = FlashCookie.Take(http);
            return Html.Render<TwoFactorSetupPage>(new { QrSvg = svg, Key = key!, Error = error });
        });

        admin.MapPost("/profile/2fa", async (HttpContext http, UserManager<User> users, IAuditLog audit, [FromForm] string code) =>
        {
            var me = await Me(http, users);
            var clean = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
            if (!await users.VerifyTwoFactorTokenAsync(me, users.Options.Tokens.AuthenticatorTokenProvider, clean))
                return FlashCookie.Redirect(http, "/admin/profile/2fa", "That code is not right. Check the time on your phone and try the current code.", error: true);
            await users.SetTwoFactorEnabledAsync(me, true);
            var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(me, 10))!.ToList();
            await AdminSession.RefreshAsync(http, me);
            await audit.WriteAsync(me.UserName ?? "", "user.2fa.on", "User", me.Id.ToString(), "");
            return Html.Render<RecoveryCodesPage>(new { Codes = codes, JustEnabled = true });
        });

        admin.MapPost("/profile/2fa/recovery", async (HttpContext http, UserManager<User> users, IAuditLog audit) =>
        {
            var me = await Me(http, users);
            if (!me.TwoFactorEnabled) return Results.Redirect("/admin/profile");
            var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(me, 10))!.ToList();
            await AdminSession.RefreshAsync(http, me);
            await audit.WriteAsync(me.UserName ?? "", "user.2fa.codes", "User", me.Id.ToString(), "new recovery codes");
            return Html.Render<RecoveryCodesPage>(new { Codes = codes, JustEnabled = false });
        });

        admin.MapPost("/profile/2fa/disable", async (HttpContext http, UserManager<User> users, AccountService accounts, [FromForm] string password) =>
        {
            var me = await Me(http, users);
            if (!await users.CheckPasswordAsync(me, password ?? "")) return FlashCookie.Redirect(http, "/admin/profile", "The password is not right.", error: true);
            await accounts.DisableTwoFactorAsync(me.Id, me.UserName ?? "");
            await AdminSession.RefreshAsync(http, await Me(http, users));
            return FlashCookie.Redirect(http, "/admin/profile", "Two-factor sign-in is off.");
        });
    }

    // ---------------------------------------------------------------- users, groups, roles, relations (admin: rw)
    private static void MapAccess(RouteGroupBuilder admin)
    {
        static void RequireAdmin(HttpContext http)
        {
            if (!AdminContext.Get(http).Perms.IsAdmin) throw new AccessDeniedException("This needs admin: rw.");
        }

        admin.MapGet("/users", async (HttpContext http, HttpRequest req, IDbContextFactory<AppDbContext> dbf) =>
        {
            var (_, error) = FlashCookie.Take(http);
            RequireAdmin(http);
            var url = ListUrl.From(req, "q", "status", "role", "size", "page");
            await using var db = await dbf.CreateDbContextAsync();
            var q = db.Users.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(url["q"]))
            {
                var like = Paging.Like(url["q"]!);
                q = q.Where(u => EF.Functions.ILike(u.UserName!, like) || EF.Functions.ILike(u.DisplayName, like) || EF.Functions.ILike(u.Email!, like));
            }
            if (url["status"] == "valid") q = q.Where(u => u.Valid);
            else if (url["status"] == "invalid") q = q.Where(u => !u.Valid);
            if (Int(url["role"]) is { } rid) q = q.Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == rid));
            var result = await q.OrderBy(u => u.UserName).ToPageAsync(Int(url["page"]), Int(url["size"]));
            var ids = result.Items.Select(u => u.Id).ToList();
            var roles = await (from ur in db.UserRoles join r in db.PermissionRoles on ur.RoleId equals r.Id where ids.Contains(ur.UserId)
                               select new { ur.UserId, r.Name }).ToListAsync();
            return Html.Render<UsersPage>(new
            {
                Result = result, Url = url,
                Roles = await db.PermissionRoles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(),
                RolesByUser = roles.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.Select(x => x.Name).Order().ToList()),
                Error = error,
            });
        });

        admin.MapGet("/users/new", (HttpContext http) =>
        {
            RequireAdmin(http);
            var (_, error) = FlashCookie.Take(http);
            return Html.Render<UserNewPage>(new { Error = error });
        });

        admin.MapPost("/users", async (HttpContext http, AccountService accounts, [FromForm] string userName, [FromForm] string? displayName,
            [FromForm] string? email, [FromForm] string password) =>
        {
            RequireAdmin(http);
            var (result, user) = await accounts.CreateAsync(userName, displayName ?? "", password, AdminContext.Get(http).Actor, email: email);
            return result.Ok ? FlashCookie.Redirect(http, $"/admin/users/{user!.Id}", "Account created. Give it roles or group permissions next.")
                             : FlashCookie.Redirect(http, "/admin/users/new", result.Error ?? "", error: true);
        });

        admin.MapPost("/users/{id:int}/2fa/disable", async (int id, HttpContext http, AccountService accounts) =>
        {
            RequireAdmin(http);
            var r = await accounts.DisableTwoFactorAsync(id, AdminContext.Get(http).Actor);
            return r.Ok ? FlashCookie.Redirect(http, $"/admin/users/{id}", "Two-factor sign-in is off for this account.") : FlashCookie.Redirect(http, $"/admin/users/{id}", r.Error ?? "", error: true);
        });

        admin.MapGet("/users/{id:int}", async (int id, HttpContext http, IDbContextFactory<AppDbContext> dbf, IPermissionService perms,
            CancellationToken ct) =>
        {
            var (message, error) = FlashCookie.Take(http);
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
            PublicContent content, IOutputCacheStore cache, IAuditLog audit,
            [FromForm] string? displayName, [FromForm] string? email, [FromForm] bool? valid) =>
        {
            RequireAdmin(http);
            var a = AdminContext.Get(http);
            if (id == a.UserId && valid != true) return FlashCookie.Redirect(http, $"/admin/users/{id}", "You cannot make your own account invalid.", error: true);
            var cleanEmail = Homepage.Accounts.AccountService.CleanEmail(email, out var emailOk);
            if (!emailOk) return FlashCookie.Redirect(http, $"/admin/users/{id}", "That email address does not look right.", error: true);
            bool wasValid;
            await using (var db = await dbf.CreateDbContextAsync())
            {
                var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id) ?? throw new NotFoundException();
                wasValid = user.Valid;
                user.DisplayName = (displayName ?? "").Trim() is { Length: > 0 } d ? d[..Math.Min(d.Length, 200)] : user.UserName ?? "";
                user.Email = cleanEmail;
                user.NormalizedEmail = cleanEmail?.ToUpperInvariant();
                await db.SaveChangesAsync();
            }
            if (wasValid != (valid == true)) await accounts.SetValidAsync(id, valid == true, a.Actor);
            await audit.WriteAsync(a.Actor, "user.update", "User", id.ToString(), "display name and email");
            content.ForgetAuthor(id);
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            return FlashCookie.Redirect(http, $"/admin/users/{id}", "Saved.");
        });

        admin.MapPost("/users/{id:int}/password", async (int id, HttpContext http, AccountService accounts, [FromForm] string password) =>
        {
            RequireAdmin(http);
            var r = await accounts.ResetPasswordAsync(id, password, AdminContext.Get(http).Actor);
            return r.Ok ? FlashCookie.Redirect(http, $"/admin/users/{id}", "Password changed.") : FlashCookie.Redirect(http, $"/admin/users/{id}", r.Error ?? "", error: true);
        });

        foreach (var kind in new[] { "groups", "roles" })
        {
            var roles = kind == "roles";
            admin.MapGet("/" + kind, async (HttpContext http, HttpRequest req, IDbContextFactory<AppDbContext> dbf) =>
            {
                var (message, error) = FlashCookie.Take(http);
                RequireAdmin(http);
                var url = ListUrl.From(req, "q", "status", "size", "page");
                var like = string.IsNullOrWhiteSpace(url["q"]) ? null : Paging.Like(url["q"]!);
                bool? valid = url["status"] switch { "valid" => true, "invalid" => false, _ => null };
                await using var db = await dbf.CreateDbContextAsync();
                PagedResult<GroupsPage.Row> result;
                if (roles)
                {
                    var q = db.PermissionRoles.AsNoTracking().AsQueryable();
                    if (like is not null) q = q.Where(r => EF.Functions.ILike(r.Name, like) || EF.Functions.ILike(r.Comment, like));
                    if (valid is { } v) q = q.Where(r => r.Valid == v);
                    result = await q.OrderBy(r => r.Name).Select(r => new GroupsPage.Row(r.Id, r.Name, r.Comment, r.Valid, false)).ToPageAsync(Int(url["page"]), Int(url["size"]));
                }
                else
                {
                    var q = db.Groups.AsNoTracking().AsQueryable();
                    if (like is not null) q = q.Where(g => EF.Functions.ILike(g.Name, like) || EF.Functions.ILike(g.Comment, like));
                    if (valid is { } v) q = q.Where(g => g.Valid == v);
                    result = await q.OrderByDescending(g => g.IsSystem).ThenBy(g => g.Name)
                        .Select(g => new GroupsPage.Row(g.Id, g.Name, g.Comment, g.Valid, g.IsSystem)).ToPageAsync(Int(url["page"]), Int(url["size"]));
                }
                return Html.Render<GroupsPage>(new { Result = result, Url = url, IsRoles = roles, Message = message, Error = error });
            });

            async Task<IResult> Editor(HttpContext http, AppDbContext db, int? id, string? message, string? error,
                string? name = null, string? comment = null, bool? valid = null)
            {
                GroupsPage.Row? row = null;
                string? usage = null;
                if (id is { } gid)
                {
                    if (roles)
                    {
                        row = await db.PermissionRoles.AsNoTracking().Where(r => r.Id == gid).Select(r => new GroupsPage.Row(r.Id, r.Name, r.Comment, r.Valid, false)).FirstOrDefaultAsync()
                              ?? throw new NotFoundException();
                        var users = await db.UserRoles.CountAsync(x => x.RoleId == gid);
                        usage = users == 1 ? "1 user has this role." : $"{users} users have this role.";
                    }
                    else
                    {
                        row = await db.Groups.AsNoTracking().Where(g => g.Id == gid).Select(g => new GroupsPage.Row(g.Id, g.Name, g.Comment, g.Valid, g.IsSystem)).FirstOrDefaultAsync()
                              ?? throw new NotFoundException();
                        if (!row.IsSystem)
                        {
                            var content = await db.ContentItems.CountAsync(c => c.GroupId == gid);
                            var media = await db.Media.CountAsync(m => m.GroupId == gid);
                            usage = $"In this group: {content} pages and posts, {media} media files.";
                        }
                    }
                }
                return Html.Render<GroupEditPage>(new
                {
                    Row = row, IsRoles = roles, Name = name ?? row?.Name ?? "", Comment = comment ?? row?.Comment ?? "", Valid = valid ?? row?.Valid ?? true,
                    UsageNote = usage, Message = message, Error = error,
                });
            }

            admin.MapGet("/" + kind + "/new", async (HttpContext http, IDbContextFactory<AppDbContext> dbf) =>
            {
                RequireAdmin(http);
                await using var db = await dbf.CreateDbContextAsync();
                return await Editor(http, db, null, null, null);
            });

            admin.MapGet("/" + kind + "/{id:int}", async (int id, HttpContext http, IDbContextFactory<AppDbContext> dbf) =>
            {
                RequireAdmin(http);
                var (message, error) = FlashCookie.Take(http);
                await using var db = await dbf.CreateDbContextAsync();
                return await Editor(http, db, id, message, error);
            });
            admin.MapPost("/" + kind, async (HttpContext http, PermissionAdminService svc, IDbContextFactory<AppDbContext> dbf, [FromForm] int? id, [FromForm] string name,
                [FromForm] string? comment, [FromForm] bool? valid) =>
            {
                var a = AdminContext.Get(http);
                int savedId;
                try
                {
                    savedId = roles ? (await svc.SaveRoleAsync(a.Perms, id, name, comment ?? "", valid == true, a.Actor)).Id
                                    : (await svc.SaveGroupAsync(a.Perms, id, name, comment ?? "", valid == true, a.Actor)).Id;
                }
                catch (Exception ex) when (ex is BlockException or ConflictException)
                {
                    await using var db = await dbf.CreateDbContextAsync();
                    return await Editor(http, db, id, null, ex.Message, name, comment, valid == true);
                }
                var noun = roles ? "Role" : "Group";
                return FlashCookie.Redirect(http, $"/admin/{kind}/{savedId}", id is null ? $"{noun} created." : "Saved.");
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
        admin.MapGet("/settings", async (HttpContext http, IDbContextFactory<AppDbContext> dbf, ServerUpdateService updates, BlogService blogs) =>
        {
            var (message, error) = FlashCookie.Take(http);
            if (!AdminContext.Get(http).Perms.IsAdmin) throw new AccessDeniedException("This needs admin: rw.");
            await using var db = await dbf.CreateDbContextAsync();
            return Html.Render<SettingsPage>(new
            {
                S = await SiteSettings.LoadAsync(db),
                Pages = await db.ContentItems.AsNoTracking().Where(c => c.Type == ContentType.Page && c.Status == ContentStatus.Published).OrderBy(c => c.Title).ToListAsync(),
                Groups = await db.Groups.AsNoTracking().Where(g => !g.IsSystem && g.Valid).OrderBy(g => g.Name).ToListAsync(),
                Blogs = await blogs.AllAsync(),
                Images = await db.Media.AsNoTracking().Where(m => m.ContentType.StartsWith("image/")).OrderBy(m => m.FileName).Take(1000).ToListAsync(),
                Update = await updates.GetStatusAsync(),
                Message = message, Error = error,
            });
        });

        admin.MapPost("/settings", async (HttpContext http, IDbContextFactory<AppDbContext> dbf, IOutputCacheStore cache, IAuditLog audit,
            [FromForm] string title, [FromForm] string? tagline, [FromForm] string? baseUrl, [FromForm] string? home, [FromForm] string? defaultGroupId,
            [FromForm] string? logoMediaId, [FromForm] string? faviconMediaId) =>
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
            // "page:12" or "blog:3": a page, or a blog's post list.
            var (homeKind, homeId) = home?.Split(':') is [var k, var v] && int.TryParse(v, out var hid) ? (k, hid) : ("", 0);
            await SiteSettings.SetAsync(db, SiteSettings.HomePageKey, homeKind == "page" ? homeId.ToString() : "");
            await SiteSettings.SetAsync(db, SiteSettings.HomeBlogKey, homeKind == "blog" ? homeId.ToString() : "");
            foreach (var (key, value) in new[] { (SiteSettings.LogoKey, logoMediaId), (SiteSettings.FaviconKey, faviconMediaId) })
            {
                int? mid = int.TryParse(value, out var m) ? m : null;
                if (mid is { } id && !await db.Media.AnyAsync(x => x.Id == id && x.ContentType.StartsWith("image/"))) throw new BlockException("Choose an image from Media.");
                await SiteSettings.SetAsync(db, key, mid?.ToString() ?? "");
            }
            if (int.TryParse(defaultGroupId, out var g)) await SiteSettings.SetAsync(db, SiteSettings.DefaultGroupKey, g.ToString());
            await db.SaveChangesAsync();
            await cache.EvictByTagAsync(SiteEndpoints.CacheTag, default);
            await audit.WriteAsync(a.Actor, "settings.save", "Settings", "site", title);
            return FlashCookie.Redirect(http, "/admin/settings", "Saved.");
        });

        admin.MapPost("/updates/check", async (HttpContext http, ServerUpdateService updates) =>
        {
            var a = AdminContext.Get(http);
            if (!a.Perms.IsAdmin) throw new AccessDeniedException("This needs admin: rw.");
            await updates.CheckNowAsync(a.Actor);
            return Results.Redirect("/admin/settings");
        });

        // Downloads, verifies and stages the update; update.sh (root) applies it a moment later and restarts the server.
        // The Settings page calls this with fetch and then polls /admin/updates/status through the restart.
        admin.MapPost("/updates/apply", async (HttpContext http, ServerUpdateService updates) =>
        {
            var a = AdminContext.Get(http);
            if (!a.Perms.IsAdmin) throw new AccessDeniedException("This needs admin: rw.");
            var before = await updates.GetStatusAsync();
            var r = await updates.UpdateNowAsync(a.Actor);
            if (!r.Ok) throw new BlockException(r.Error ?? "The update could not be started.");
            if (http.Request.Headers.Accept.Any(x => x?.Contains("application/json", StringComparison.Ordinal) == true))
                return Results.Json(new { ok = true, from = before.CurrentVersion, to = before.Latest?.Version });
            return FlashCookie.Redirect(http, "/admin/settings", "Update staged. The site restarts in a moment.");
        });

        admin.MapGet("/updates/status", async (HttpContext http, ServerUpdateService updates) =>
        {
            if (!AdminContext.Get(http).Perms.IsAdmin) throw new AccessDeniedException("This needs admin: rw.");
            var st = await updates.GetStatusAsync();
            return Results.Json(new
            {
                version = st.CurrentVersion,
                run = st.LastRun is { } run
                    ? new { state = run.State.ToString(), from = run.FromVersion, to = run.ToVersion, message = run.Message, finishedAt = run.FinishedAtUtc }
                    : null,
            });
        });

        admin.MapGet("/audit", async (HttpContext http, HttpRequest req, IDbContextFactory<AppDbContext> dbf) =>
        {
            if (!AdminContext.Get(http).Perms.HasSystem(SystemGroups.Audit, Perm.Ro)) throw new AccessDeniedException("The audit log needs audit: ro.");
            var url = ListUrl.From(req, "q", "area", "from", "to", "size", "page");
            await using var db = await dbf.CreateDbContextAsync();
            var q = db.AuditLog.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(url["q"]))
            {
                var like = Paging.Like(url["q"]!);
                q = q.Where(x => EF.Functions.ILike(x.Actor, like) || EF.Functions.ILike(x.Action, like) || EF.Functions.ILike(x.Details, like)
                                 || EF.Functions.ILike(x.EntityId, like) || (x.Ip != null && EF.Functions.ILike(x.Ip, like))
                                 || (x.SignInIp != null && EF.Functions.ILike(x.SignInIp, like)));
            }
            if (url["area"] is { Length: > 0 } area && AuditPage.Areas.Any(x => x.Key == area)) q = q.Where(x => x.Action.StartsWith(area));
            if (DateTime.TryParse(url["from"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var from))
                q = q.Where(x => x.AtUtc >= from.ToUniversalTime());
            if (DateTime.TryParse(url["to"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var to))
                q = q.Where(x => x.AtUtc < to.AddDays(1).ToUniversalTime());
            return Html.Render<AuditPage>(new { Result = await q.OrderByDescending(x => x.Id).ToPageAsync(Int(url["page"]), Int(url["size"])), Url = url });
        });
    }

    // ---------------------------------------------------------------- helpers
    private static async Task<IResult> ContentSettings(int id, HttpContext http, ContentService content, IDbContextFactory<AppDbContext> dbf,
        BlogService blogs, string? error)
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
            Blogs = await blogs.AllAsync(),
            CanSetHome = a.Perms.IsAdmin,
            IsHome = (await SiteSettings.LoadAsync(db)).HomePageId == id,
        }, error is null ? 200 : 400);
    }

    private static List<Group> CreatableGroups(EffectivePermissions p, List<Group> groups) =>
        groups.Where(g => g.Valid && !g.IsSystem && p.Has(g.Id, Perm.Create)).OrderBy(g => g.Name).ToList();

    private static EditorTarget ParseTarget(string s) =>
        EditorTarget.TryParse(s, out var t) ? t.Value : throw new NotFoundException();

    private static string? Do(Action a) { a(); return null; }

    private static int? Int(string? s) => int.TryParse(s, out var v) ? v : null;

    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<User>().HashPassword(new User(), Guid.NewGuid().ToString()));

    private static bool DummyPasswordCheck(string password)
    {
        new PasswordHasher<User>().VerifyHashedPassword(new User(), DummyHash.Value, password);
        return false;
    }

    private static string Clip(string s, int max) { s = s.Trim(); return s.Length <= max ? s : s[..max]; }

    private static double Num(Microsoft.Extensions.Primitives.StringValues v, double fallback) =>
        double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;
}

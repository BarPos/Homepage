using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Homepage.Data;

/// <summary>
/// Identity's user tables (no Identity roles: authorization uses the OTRS-style groups below) plus the CMS model.
/// Table names are snake_case; column names keep the C# property names.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityUserContext<User, int>(options)
{
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Role> PermissionRoles => Set<Role>();
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();
    public DbSet<RoleGroup> RoleGroups => Set<RoleGroup>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<ContentItem> ContentItems => Set<ContentItem>();
    public DbSet<Blog> Blogs => Set<Blog>();
    public DbSet<Revision> Revisions => Set<Revision>();
    public DbSet<MediaItem> Media => Set<MediaItem>();
    public DbSet<Menu> Menus => Set<Menu>();
    public DbSet<Theme> Themes => Set<Theme>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<UpdateRun> UpdateRuns => Set<UpdateRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(u => u.DisplayName).HasMaxLength(200);
        });
        b.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<int>>().ToTable("user_claims");
        b.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<int>>().ToTable("user_logins");
        b.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<int>>().ToTable("user_tokens");

        b.Entity<Group>(e =>
        {
            e.ToTable("groups");
            e.Property(g => g.Name).HasMaxLength(100);
            e.Property(g => g.Comment).HasMaxLength(500);
            e.HasIndex(g => g.Name).IsUnique();
        });
        b.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.Property(r => r.Name).HasMaxLength(100);
            e.Property(r => r.Comment).HasMaxLength(500);
            e.HasIndex(r => r.Name).IsUnique();
        });
        b.Entity<UserGroup>(e =>
        {
            e.ToTable("user_groups");
            e.HasKey(x => new { x.UserId, x.GroupId, x.Permission });
            e.Property(x => x.Permission).HasMaxLength(20);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<RoleGroup>(e =>
        {
            e.ToTable("role_groups");
            e.HasKey(x => new { x.RoleId, x.GroupId, x.Permission });
            e.Property(x => x.Permission).HasMaxLength(20);
            e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ContentItem>(e =>
        {
            e.ToTable("content_items");
            e.Property(c => c.Slug).HasMaxLength(200);
            e.Property(c => c.Title).HasMaxLength(300);
            e.Property(c => c.Excerpt).HasMaxLength(1000);
            e.Property(c => c.Blocks).HasColumnType("jsonb");
            e.Property(c => c.DraftBlocks).HasColumnType("jsonb");
            // Page addresses are unique among pages; post addresses only within their blog.
            e.HasIndex(c => c.Slug).IsUnique().HasFilter("\"Type\" = 0").HasDatabaseName("IX_content_items_page_slug");
            e.HasIndex(c => new { c.BlogId, c.Slug }).IsUnique().HasFilter("\"Type\" = 1").HasDatabaseName("IX_content_items_post_slug");
            e.HasOne<Blog>().WithMany().HasForeignKey(c => c.BlogId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(c => new { c.Status, c.PublishedAtUtc });
            e.HasIndex(c => c.GroupId);
            e.HasOne<Group>().WithMany().HasForeignKey(c => c.GroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ContentItem>().WithMany().HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<User>().WithMany().HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.SetNull);
            e.HasGeneratedTsVectorColumn(c => c.SearchVector!, "simple", c => new { c.Title, c.SearchText })
                .HasIndex(c => c.SearchVector).HasMethod("GIN");
            e.Property(c => c.Version).IsRowVersion();
        });
        b.Entity<Blog>(e =>
        {
            e.ToTable("blogs");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Slug).HasMaxLength(80);
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => x.Slug).IsUnique();
        });
        b.Entity<Revision>(e =>
        {
            e.ToTable("revisions");
            e.Property(r => r.Blocks).HasColumnType("jsonb");
            e.HasIndex(r => r.ContentItemId);
            e.HasOne<ContentItem>().WithMany().HasForeignKey(r => r.ContentItemId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MediaItem>(e =>
        {
            e.ToTable("media");
            e.Property(m => m.FileName).HasMaxLength(255);
            e.Property(m => m.StoredName).HasMaxLength(100);
            e.Property(m => m.ContentType).HasMaxLength(100);
            e.Property(m => m.AltText).HasMaxLength(500);
            e.HasIndex(m => m.StoredName).IsUnique();
            e.HasOne<Group>().WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Menu>(e =>
        {
            e.ToTable("menus");
            e.Property(m => m.Key).HasMaxLength(50);
            e.Property(m => m.Items).HasColumnType("jsonb");
            e.HasIndex(m => m.Key).IsUnique();
        });
        b.Entity<Theme>(e =>
        {
            e.ToTable("themes");
            e.Property(t => t.Name).HasMaxLength(100);
            e.Property(t => t.Slug).HasMaxLength(100);
            e.Property(t => t.Tokens).HasColumnType("jsonb");
            e.Property(t => t.HeaderBlocks).HasColumnType("jsonb");
            e.Property(t => t.FooterBlocks).HasColumnType("jsonb");
            e.HasIndex(t => t.Slug).IsUnique();
            e.Property(t => t.Version).IsRowVersion();
        });
        b.Entity<Setting>(e =>
        {
            e.ToTable("settings");
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(100);
        });
        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log");
            e.HasIndex(a => a.AtUtc);
            e.Property(a => a.Actor).HasMaxLength(200);
            e.Property(a => a.Action).HasMaxLength(100);
            e.Property(a => a.Entity).HasMaxLength(100);
            e.Property(a => a.EntityId).HasMaxLength(100);
        });
        b.Entity<UpdateRun>(e =>
        {
            e.ToTable("update_runs");
            e.Property(r => r.FromVersion).HasMaxLength(32);
            e.Property(r => r.ToVersion).HasMaxLength(32);
            e.Property(r => r.RequestedBy).HasMaxLength(200);
        });
    }
}

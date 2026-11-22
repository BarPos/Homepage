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
    public DbSet<Analytics.PageView> PageViews => Set<Analytics.PageView>();
    public DbSet<Forms.Form> Forms => Set<Forms.Form>();
    public DbSet<Forms.FormSubmission> FormSubmissions => Set<Forms.FormSubmission>();
    public DbSet<Mail.EmailTemplate> EmailTemplates => Set<Mail.EmailTemplate>();
    public DbSet<Mail.MailingList> MailingLists => Set<Mail.MailingList>();
    public DbSet<Mail.Subscriber> Subscribers => Set<Mail.Subscriber>();
    public DbSet<Mail.Campaign> Campaigns => Set<Mail.Campaign>();
    public DbSet<Mail.OutboxMessage> Outbox => Set<Mail.OutboxMessage>();
    public DbSet<Analytics.AnalyticsSalt> AnalyticsSalts => Set<Analytics.AnalyticsSalt>();

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
            // Page addresses are unique among siblings: top-level pages by slug, subpages by parent and slug.
            e.HasIndex(c => c.Slug).IsUnique().HasFilter("\"Type\" = 0 AND \"ParentId\" IS NULL").HasDatabaseName("IX_content_items_page_slug");
            e.HasIndex(c => new { c.ParentId, c.Slug }).IsUnique().HasFilter("\"Type\" = 0 AND \"ParentId\" IS NOT NULL").HasDatabaseName("IX_content_items_subpage_slug");
            e.HasIndex(c => new { c.BlogId, c.Slug }).IsUnique().HasFilter("\"Type\" = 1").HasDatabaseName("IX_content_items_post_slug");
            e.HasOne<Blog>().WithMany().HasForeignKey(c => c.BlogId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(c => new { c.Status, c.PublishedAtUtc });
            e.HasIndex(c => c.GroupId);
            e.HasOne<Group>().WithMany().HasForeignKey(c => c.GroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ContentItem>().WithMany().HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<User>().WithMany().HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Theme>().WithMany().HasForeignKey(c => c.ThemeId).OnDelete(DeleteBehavior.SetNull);
            e.Property(c => c.CustomCss).HasMaxLength(100_000);
            e.Property(c => c.InheritTheme).HasDefaultValue(true);
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
            e.HasIndex(x => x.Slug).IsUnique().HasFilter("\"ParentPageId\" IS NULL");
            e.HasIndex(x => new { x.ParentPageId, x.Slug }).IsUnique().HasFilter("\"ParentPageId\" IS NOT NULL");
            e.HasOne<ContentItem>().WithMany().HasForeignKey(x => x.ParentPageId).OnDelete(DeleteBehavior.Restrict);
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
            e.Property(t => t.DraftTokens).HasColumnType("jsonb");
            e.Property(t => t.DraftHeaderBlocks).HasColumnType("jsonb");
            e.Property(t => t.DraftFooterBlocks).HasColumnType("jsonb");
            e.Ignore(t => t.HasDraft);
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
            e.Property(a => a.Ip).HasMaxLength(64);
            e.Property(a => a.SignInIp).HasMaxLength(64);
        });
        b.Entity<Analytics.PageView>(e =>
        {
            e.ToTable("page_views");
            e.HasIndex(p => p.AtUtc);
            e.HasIndex(p => new { p.VisitorId, p.AtUtc });
            e.Property(p => p.Path).HasMaxLength(300);
            e.Property(p => p.ReferrerHost).HasMaxLength(200);
            e.Property(p => p.Source).HasMaxLength(100);
            e.Property(p => p.Channel).HasMaxLength(20);
            e.Property(p => p.UtmSource).HasMaxLength(100);
            e.Property(p => p.UtmMedium).HasMaxLength(100);
            e.Property(p => p.UtmCampaign).HasMaxLength(100);
            e.Property(p => p.Country).HasMaxLength(2);
            e.Property(p => p.Browser).HasMaxLength(40);
            e.Property(p => p.Os).HasMaxLength(40);
            e.Property(p => p.Device).HasMaxLength(20);
            e.Property(p => p.PageKey).HasMaxLength(40);
        });
        b.Entity<Forms.Form>(e =>
        {
            e.ToTable("forms");
            e.Property(f => f.Name).HasMaxLength(100);
            e.Property(f => f.Fields).HasColumnType("jsonb");
            e.Property(f => f.SubmitLabel).HasMaxLength(60);
            e.Property(f => f.SuccessMessage).HasMaxLength(2000);
            e.Property(f => f.SuccessUrl).HasMaxLength(1000);
            e.Property(f => f.NotifyEmails).HasMaxLength(1000);
            e.Property(f => f.ConsentFieldKey).HasMaxLength(40);
            e.HasOne<Mail.MailingList>().WithMany().HasForeignKey(f => f.MailingListId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Forms.FormSubmission>(e =>
        {
            e.ToTable("form_submissions");
            e.Property(s => s.Data).HasColumnType("jsonb");
            e.Property(s => s.PageUrl).HasMaxLength(300);
            e.HasIndex(s => new { s.FormId, s.AtUtc });
            e.HasOne<Forms.Form>().WithMany().HasForeignKey(s => s.FormId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Mail.EmailTemplate>(e =>
        {
            e.ToTable("email_templates");
            e.Property(t => t.Key).HasMaxLength(60);
            e.Property(t => t.Name).HasMaxLength(100);
            e.Property(t => t.Subject).HasMaxLength(200);
            e.HasIndex(t => t.Key).IsUnique().HasFilter("\"Key\" IS NOT NULL");
        });
        b.Entity<Mail.MailingList>(e =>
        {
            e.ToTable("mailing_lists");
            e.Property(l => l.Name).HasMaxLength(100);
            e.Property(l => l.Description).HasMaxLength(500);
        });
        b.Entity<Mail.Subscriber>(e =>
        {
            e.ToTable("subscribers");
            e.Property(s => s.Email).HasMaxLength(254);
            e.Property(s => s.Name).HasMaxLength(100);
            e.Property(s => s.Token).HasMaxLength(64);
            e.Property(s => s.Source).HasMaxLength(100);
            e.HasIndex(s => new { s.ListId, s.Email }).IsUnique();
            e.HasIndex(s => s.Token).IsUnique();
            e.HasOne<Mail.MailingList>().WithMany().HasForeignKey(s => s.ListId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Mail.Campaign>(e =>
        {
            e.ToTable("campaigns");
            e.Property(c => c.Subject).HasMaxLength(200);
            e.Property(c => c.CreatedBy).HasMaxLength(200);
            e.HasOne<Mail.MailingList>().WithMany().HasForeignKey(c => c.ListId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Mail.OutboxMessage>(e =>
        {
            e.ToTable("mail_outbox");
            e.Property(m => m.To).HasMaxLength(254);
            e.Property(m => m.ToName).HasMaxLength(200);
            e.Property(m => m.Subject).HasMaxLength(200);
            e.Property(m => m.ReplyTo).HasMaxLength(254);
            e.Property(m => m.UnsubscribeUrl).HasMaxLength(500);
            e.Property(m => m.Kind).HasMaxLength(60);
            e.Property(m => m.Error).HasMaxLength(500);
            e.HasIndex(m => new { m.Status, m.NextAttemptAtUtc });
            e.HasIndex(m => m.CampaignId);
        });
        b.Entity<Analytics.AnalyticsSalt>(e =>
        {
            e.ToTable("analytics_salts");
            e.HasKey(s => s.Day);
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

using Homepage.Data;
using Microsoft.EntityFrameworkCore;

namespace Homepage.Common;

/// <summary>Site-wide settings (title, tagline, home page, default group) in the settings table.</summary>
public sealed record SiteSettings(string Title, string Tagline, int? HomePageId, int? DefaultGroupId, string BaseUrl)
{
    public const string TitleKey = "site.title";
    public const string TaglineKey = "site.tagline";
    public const string HomePageKey = "site.homePageId";
    public const string DefaultGroupKey = "site.defaultGroupId";
    public const string BaseUrlKey = "site.baseUrl";

    public static async Task<SiteSettings> LoadAsync(AppDbContext db, CancellationToken ct = default)
    {
        var all = await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith("site.")).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        return new SiteSettings(
            all.GetValueOrDefault(TitleKey) ?? "Homepage",
            all.GetValueOrDefault(TaglineKey) ?? "",
            int.TryParse(all.GetValueOrDefault(HomePageKey), out var h) ? h : null,
            int.TryParse(all.GetValueOrDefault(DefaultGroupKey), out var g) ? g : null,
            all.GetValueOrDefault(BaseUrlKey) ?? "");
    }

    public static async Task SetAsync(AppDbContext db, string key, string value, CancellationToken ct = default)
    {
        var s = await db.Settings.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (s is null) db.Settings.Add(new Setting { Key = key, Value = value });
        else s.Value = value;
    }
}

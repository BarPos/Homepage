namespace Homepage.Common;

/// <summary>One page of a list. Pages are 1-based; the total is counted in SQL with the same filters.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
    public int From => Total == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int To => Math.Min(Total, Page * PageSize);
}

public static class Paging
{
    public const int DefaultPageSize = 25;
    public static readonly int[] PageSizes = [10, 25, 50, 100];

    public static (int Page, int Size) Normalize(int? page, int? size)
    {
        var s = size is { } v && PageSizes.Contains(v) ? v : DefaultPageSize;
        return (Math.Max(1, page ?? 1), s);
    }

    public static async Task<PagedResult<T>> ToPageAsync<T>(this IQueryable<T> query, int? page, int? size, CancellationToken ct = default)
    {
        var (p, s) = Normalize(page, size);
        var total = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(query, ct);
        var pages = Math.Max(1, (total + s - 1) / s);
        if (p > pages) p = pages;
        var items = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(query.Skip((p - 1) * s).Take(s), ct);
        return new PagedResult<T>(items, total, p, s);
    }

    /// <summary>A LIKE pattern for a user's search text, with % and _ escaped.</summary>
    public static string Like(string text) => "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}

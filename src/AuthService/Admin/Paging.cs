using Microsoft.EntityFrameworkCore;

namespace AuthService.Admin;

/// <summary>
/// One page of a list that can grow large (users, open sessions): <c>?page=</c> (from 1) and <c>?pageSize=</c>.
/// Out-of-range values are brought into range rather than refused (page 1 to <see cref="MaxPage"/>, size 1 to
/// <see cref="MaxPageSize"/>, default <see cref="DefaultPageSize"/>), so a stale or hand-edited address still shows
/// something. The answer is <c>{ items, total, page, pageSize }</c>, total counting every match.
/// </summary>
public sealed record PageRequest(int Page, int PageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    // The largest page whose offset ((page - 1) * pageSize) still fits an int at the largest page size. A larger
    // page would overflow into a negative OFFSET, which PostgreSQL refuses (a 500 instead of an empty page).
    public const int MaxPage = int.MaxValue / MaxPageSize;

    public static PageRequest From(int? page, int? pageSize) =>
        new(Math.Clamp(page ?? 1, 1, MaxPage), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));

    /// <summary>The page of <paramref name="ordered"/> (which must have a stable order), and the total.</summary>
    public async Task<PageResult<T>> OfAsync<T>(IQueryable<T> ordered, CancellationToken cancellationToken)
    {
        var total = await ordered.CountAsync(cancellationToken);
        var items = await ordered.Skip((Page - 1) * PageSize).Take(PageSize).ToListAsync(cancellationToken);
        return new PageResult<T>(items, total, Page, PageSize);
    }
}

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public static class Search
{
    /// <summary>
    /// A <c>?search=</c> text as an ILIKE "contains" pattern, or null for no search. % and _ (and the escape
    /// character \) are escaped: the admin searches for the text they typed, not a pattern.
    /// </summary>
    public static string? ContainsPattern(string? search)
    {
        var trimmed = search?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var escaped = trimmed.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
        return $"%{escaped}%";
    }
}

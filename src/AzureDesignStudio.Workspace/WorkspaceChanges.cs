using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Workspace;

/// <summary>
/// Change feed persistence (plan 9.1): ARG change records stored per collection run so
/// drift detection (9.2) and the changes UI work offline. Idempotent by
/// (TargetResourceId, ChangeKey) - re-collecting an overlapping window adds nothing.
/// </summary>
public sealed class WorkspaceChanges(WorkspaceDb db)
{
    /// <summary>Inserts all new entries; returns how many were actually stored.</summary>
    public async Task<int> SaveBatchAsync(string? scanId, IReadOnlyList<ChangeFeedRecord> entries)
    {
        if (entries.Count == 0) return 0;
        await using var ctx = db.CreateContext();
        var ids = entries.Select(e => e.TargetResourceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existing = (await ctx.ChangeFeed
                .Where(c => ids.Contains(c.TargetResourceId))
                .Select(c => new { c.TargetResourceId, c.ChangeKey })
                .ToListAsync())
            .Select(x => Key(x.TargetResourceId, x.ChangeKey))
            .ToHashSet();
        var added = 0;
        foreach (var entry in entries)
        {
            if (existing.Contains(Key(entry.TargetResourceId, entry.ChangeKey))) continue;
            entry.ScanId = scanId;
            ctx.ChangeFeed.Add(entry);
            existing.Add(Key(entry.TargetResourceId, entry.ChangeKey));
            added++;
        }
        await ctx.SaveChangesAsync();
        return added;
    }

    /// <summary>Newest-first feed for the window, optionally restricted to scanned resource ids.</summary>
    public async Task<IReadOnlyList<ChangeFeedRecord>> ListSinceAsync(
        DateTimeOffset since, IReadOnlySet<string>? resourceIds = null, int take = 500)
    {
        await using var ctx = db.CreateContext();
        // DateTimeOffset comparison does not translate on SQLite (TEXT storage) and the
        // feed is window-bounded and small - pull and filter/sort in memory.
        var rows = (await ctx.ChangeFeed.ToListAsync()).Where(c => c.ChangeTime > since).ToList();
        IEnumerable<ChangeFeedRecord> feed = rows;
        if (resourceIds is not null)
            feed = feed.Where(c => resourceIds.Contains(c.TargetResourceId));
        return feed.OrderByDescending(c => c.ChangeTime).Take(take).ToList();
    }

    /// <summary>Newest change per resource in one pass - the "what moved since the scan" shape.</summary>
    public async Task<IReadOnlyDictionary<string, ChangeFeedRecord>> LatestByResourceAsync(
        DateTimeOffset since, IEnumerable<string> resourceIds)
    {
        var ids = new HashSet<string>(resourceIds, StringComparer.OrdinalIgnoreCase);
        var rows = await ListSinceAsync(since, ids, take: int.MaxValue);
        var latest = new Dictionary<string, ChangeFeedRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            if (!latest.ContainsKey(row.TargetResourceId))
                latest[row.TargetResourceId] = row;
        return latest;
    }

    private static string Key(string resourceId, string changeKey) =>
        $"{resourceId.ToUpperInvariant()}|{changeKey}";
}



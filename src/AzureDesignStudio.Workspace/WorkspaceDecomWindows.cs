using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Workspace;

/// <summary>
/// v2 13.5: decommission window persistence - one row per window, upserted on every
/// execution/release outcome so the release verb re-sends the stored scopes (selection
/// drift-proof) and an offline reopen shows the last known state.
/// </summary>
public sealed class WorkspaceDecomWindows(WorkspaceDb db)
{
    /// <summary>The window row for one subscription + window id; null when never executed.</summary>
    public async Task<DecomWindowModel?> GetAsync(string subscriptionId, string windowId)
    {
        await using var ctx = db.CreateContext();
        return await ctx.DecomWindows.FirstOrDefaultAsync(w =>
            w.SubscriptionId == subscriptionId && w.WindowId == windowId);
    }

    /// <summary>Most recently updated window row (the offline banner when no runbook is open).</summary>
    public async Task<DecomWindowModel?> LatestAsync()
    {
        await using var ctx = db.CreateContext();
        // Ordered in memory: the SQLite provider cannot translate ORDER BY over a
        // DateTimeOffset column and throws NotSupportedException. One row per window.
        var windows = await ctx.DecomWindows.AsNoTracking().ToListAsync();
        return windows.OrderByDescending(w => w.UpdatedAtUtc).FirstOrDefault();
    }

    /// <summary>Inserts or refreshes the row keyed by WindowId; the outcome fields always
    /// reflect the newest execution/release, the identity fields are stable per window.</summary>
    public async Task UpsertAsync(DecomWindowModel window)
    {
        await using var ctx = db.CreateContext();
        var existing = await ctx.DecomWindows.FirstOrDefaultAsync(w => w.WindowId == window.WindowId);
        if (existing is null)
        {
            ctx.DecomWindows.Add(window);
        }
        else
        {
            existing.ScanId = window.ScanId;
            existing.SubscriptionId = window.SubscriptionId;
            existing.PlanHash = window.PlanHash;
            existing.ExecutionHash = window.ExecutionHash;
            existing.ScopesJson = window.ScopesJson;
            existing.State = window.State;
            existing.RowsJson = window.RowsJson;
            existing.UpdatedAtUtc = window.UpdatedAtUtc;
        }
        await ctx.SaveChangesAsync();
    }
}

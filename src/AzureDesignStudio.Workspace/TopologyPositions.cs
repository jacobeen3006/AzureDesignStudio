using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Workspace;

/// <summary>
/// Persisted node placements (plan 8.2). Keyed by ARM resource id, not scan id: rescans
/// rediscover the same id and find their position. Coordinates are local to the frame
/// container the node sat in, matching TopologyLayout's SavedPos contract.
/// </summary>
public sealed class TopologyPositions(WorkspaceDb db)
{
    public async Task<Dictionary<string, (string ContainerId, double X, double Y)>> GetAllAsync()
    {
        using var context = db.CreateContext();
        return (await context.NodePositions.AsNoTracking().ToListAsync())
            .ToDictionary(p => p.NodeId, p => (p.ContainerId, p.X, p.Y), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Drag-end write; also used to seed positions for freshly imported layouts.</summary>
    public async Task SaveAsync(string nodeId, string containerId, double x, double y)
    {
        using var context = db.CreateContext();
        var row = await context.NodePositions.FindAsync(nodeId);
        if (row is null)
            context.NodePositions.Add(new NodePositionRecord
            {
                NodeId = nodeId,
                ContainerId = containerId,
                X = x,
                Y = y,
                SavedAt = DateTimeOffset.UtcNow,
            });
        else
        {
            row.ContainerId = containerId;
            row.X = x;
            row.Y = y;
            row.SavedAt = DateTimeOffset.UtcNow;
        }

        await context.SaveChangesAsync();
    }
    /// <summary>Forget placements for resources that no longer exist (deleted since last scan).</summary>
    public async Task<int> PruneAsync(IReadOnlyCollection<string> liveResourceIds)
    {
        var live = new HashSet<string>(liveResourceIds, StringComparer.OrdinalIgnoreCase);
        using var context = db.CreateContext();
        var all = await context.NodePositions.AsNoTracking().ToListAsync();
        var stale = all.Where(p => !live.Contains(p.NodeId)).ToList();
        if (stale.Count == 0) return 0;
        context.NodePositions.RemoveRange(stale);
        await context.SaveChangesAsync();
        return stale.Count;
    }
}

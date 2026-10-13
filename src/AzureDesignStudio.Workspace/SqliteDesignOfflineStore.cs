using AzureDesignStudio.Core.Common;
using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Workspace;

/// <summary>
/// SQLite-backed <see cref="IDesignOfflineStore"/>: replaces the v1 per-file pending
/// cache. A design is pending while <c>SyncedAt</c> is null; a successful server sync
/// clears it (same contract the file store had).
/// </summary>
public sealed class SqliteDesignOfflineStore(WorkspaceDb workspace) : IDesignOfflineStore
{
    public async Task SaveAsync(string name, string data)
    {
        await using var db = workspace.CreateContext();
        var row = await db.Designs.FirstOrDefaultAsync(d => d.Name == name);
        if (row == null)
            db.Designs.Add(new DesignRecord { Name = name, Data = data, SavedAt = DateTimeOffset.UtcNow });
        else
        {
            row.Data = data;
            row.SavedAt = DateTimeOffset.UtcNow;
            row.SyncedAt = null; // re-editing a synced design marks it pending again
        }
        await db.SaveChangesAsync();
    }

    public async Task<string?> TryLoadAsync(string name)
    {
        await using var db = workspace.CreateContext();
        return await db.Designs.Where(d => d.Name == name).Select(d => d.Data).FirstOrDefaultAsync();
    }

    public async Task<IList<string>> ListAsync()
    {
        await using var db = workspace.CreateContext();
        return await db.Designs.OrderBy(d => d.Name).Select(d => d.Name).ToListAsync();
    }

    public async Task DeleteAsync(string name)
    {
        await using var db = workspace.CreateContext();
        await db.Designs.Where(d => d.Name == name).ExecuteDeleteAsync();
    }

    public async Task<IList<(string Name, string Data)>> ListPendingAsync()
    {
        await using var db = workspace.CreateContext();
        var rows = await db.Designs
            .Where(d => d.SyncedAt == null)
            .Select(d => new { d.Name, d.Data, d.SavedAt })
            .ToListAsync();
        // SQLite cannot ORDER BY DateTimeOffset; sync queue is small, order client-side.
        return rows.OrderBy(r => r.SavedAt).Select(r => (r.Name, r.Data)).ToList();
    }

    /// <summary>Marks the design synced: server copy is now authoritative, local pending copy goes away.</summary>
    public Task ClearPendingAsync(string name) => DeleteAsync(name);
}

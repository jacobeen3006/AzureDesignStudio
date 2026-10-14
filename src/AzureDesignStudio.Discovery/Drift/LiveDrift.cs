using AzureDesignStudio.Discovery.ChangeFeed;
using AzureDesignStudio.Discovery.Inventory;

namespace AzureDesignStudio.Discovery.Drift;

public enum LiveDriftStatus { New, Deleted, Modified }

/// <summary>One scanned resource touched after the scan was taken.</summary>
public sealed record LiveDriftItem(
    string ResourceId,
    string ResourceType,
    LiveDriftStatus Status,
    DateTimeOffset ChangedAt,
    string? ChangedBy,
    string? ClientType,
    string? Operation);

/// <summary>
/// Scan↔now drift (v2 plan 9.2): overlays the ARG change feed on a snapshot set -
/// "your scan is X hours old and these resources moved since". Newest change per
/// resource only; the change feed carries no before/after values, so drift is flagged
/// with the caller/operation, not a property diff (that needs a fresh scan).
/// </summary>
public static class LiveDrift
{
    public static IReadOnlyList<LiveDriftItem> Overlay(
        IReadOnlyList<InventoryItem> snapshot,
        IReadOnlyList<ChangeFeedEntry> changes,
        DateTimeOffset scannedAt)
    {
        var scanned = new HashSet<string>(snapshot.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        var byResource = new Dictionary<string, ChangeFeedEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in changes)
        {
            if (c.ChangeTime <= scannedAt) continue;
            if (!byResource.TryGetValue(c.TargetResourceId, out var seen) || c.ChangeTime > seen.ChangeTime)
                byResource[c.TargetResourceId] = c;
        }

        var items = new List<LiveDriftItem>(byResource.Count);
        foreach (var (id, c) in byResource)
        {
            var status = scanned.Contains(id)
                ? c.ChangeType.Equals("Delete", StringComparison.OrdinalIgnoreCase)
                    ? LiveDriftStatus.Deleted
                    : LiveDriftStatus.Modified
                : LiveDriftStatus.New;
            items.Add(new LiveDriftItem(id, c.TargetResourceType, status, c.ChangeTime, c.ChangedBy, c.ClientType, c.Operation));
        }

        return items
            .OrderByDescending(i => i.ChangedAt)
            .ThenBy(i => i.ResourceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

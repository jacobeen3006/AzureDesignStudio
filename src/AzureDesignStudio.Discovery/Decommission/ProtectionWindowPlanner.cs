using System.Security.Cryptography;
using System.Text;
using AzureDesignStudio.Discovery.Inventory;

namespace AzureDesignStudio.Discovery.Decommission;

/// <summary>A change window [68]: protections go on at Start and come off at End.
/// The window id is the ownership token - release matches locks by it, never by anything else.</summary>
public sealed record ProtectionWindow(string WindowId, DateTimeOffset Start, DateTimeOffset End);

public sealed record ProposedLock(string Scope, string Name, ArmLockLevel Level, string Notes);

public sealed record ProtectionPlan(
    IReadOnlyList<ProposedLock> Apply,
    IReadOnlyList<string> AlreadyProtected,
    string LockName);

public sealed record LockReleasePlan(
    IReadOnlyList<ArmLock> Remove,
    IReadOnlyList<ArmLock> LeftInPlace);

/// <summary>[68] The safety belt for anything marked critical: put `CanNotDelete` locks on the
/// targets before a change window, remove them after. Two invariants keep this from becoming a
/// liability: locks carry a deterministic name derived from the window id, and release removes
/// only locks with that own-prefix - a foreign lock is never touched, whatever its level or name
/// similarity. (Stack `denySettings` protection rides the 12.3 stack action itself, not here.)</summary>
public static class ProtectionWindowPlanner
{
    public const string LockPrefix = "ads-decom-";

    /// <summary>Deterministic per-window lock name (3-64 chars, ARM-safe alphabet).</summary>
    public static string LockNameFor(string windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId))
            throw new ArgumentException("window id must not be empty", nameof(windowId));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(windowId)))[..8].ToLowerInvariant();
        return LockPrefix + hash;
    }

    public static string LockNameFor(ProtectionWindow window)
    {
        if (window.End <= window.Start)
            throw new ArgumentOutOfRangeException(nameof(window), "window end must follow its start");
        return LockNameFor(window.WindowId);
    }

    public static ProtectionPlan Protect(
        IReadOnlyList<InventoryItem> inventory,
        IEnumerable<string> candidateIds,
        ProtectionWindow window,
        IReadOnlyList<ArmLock> existingLocks)
    {
        var name = LockNameFor(window);
        var apply = new List<ProposedLock>();
        var already = new List<string>();
        foreach (var raw in candidateIds)
        {
            var id = raw.Trim();
            var item = inventory.FirstOrDefault(i => string.Equals(i.Id.Trim(), id, StringComparison.OrdinalIgnoreCase));
            if (item is null) continue; // unknown candidates get no protection; the planner already refuses them
            if (IsDeleteProtected(id, existingLocks))
                already.Add(item.Id);
            else
                apply.Add(new ProposedLock(item.Id, name, ArmLockLevel.CanNotDelete,
                    $"Azure Design Studio decommission window {window.Start:O} .. {window.End:O} (window '{window.WindowId}')"));
        }

        apply.Sort((a, b) => string.CompareOrdinal(a.Scope.ToLowerInvariant(), b.Scope.ToLowerInvariant()));
        already.Sort((a, b) => string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant()));
        return new ProtectionPlan(apply, already, name);
    }

    /// <summary>Release after the window: own-prefix locks only (optionally narrowed to one
    /// window), every foreign lock reported as left in place - the operator's own locks survive
    /// a release run by construction.</summary>
    public static LockReleasePlan Release(IReadOnlyList<ArmLock> existingLocks, string? windowId = null)
    {
        var target = windowId is null ? null : LockNameFor(windowId);
        var remove = new List<ArmLock>();
        var left = new List<ArmLock>();
        foreach (var lockItem in existingLocks)
        {
            var mine = lockItem.Name.StartsWith(LockPrefix, StringComparison.Ordinal) &&
                       (target is null || string.Equals(lockItem.Name, target, StringComparison.Ordinal));
            (mine ? remove : left).Add(lockItem);
        }

        remove.Sort((a, b) => string.CompareOrdinal(a.Scope.ToLowerInvariant(), b.Scope.ToLowerInvariant()));
        left.Sort((a, b) => string.CompareOrdinal(a.Scope.ToLowerInvariant(), b.Scope.ToLowerInvariant()));
        return new LockReleasePlan(remove, left);
    }

    private static bool IsDeleteProtected(string id, IReadOnlyList<ArmLock> locks)
    {
        foreach (var lockItem in locks)
        {
            if (lockItem.Level is ArmLockLevel.DenyAssignments) continue; // does not block deletes
            var scope = lockItem.Scope.TrimEnd('/');
            if (string.Equals(id, scope, StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

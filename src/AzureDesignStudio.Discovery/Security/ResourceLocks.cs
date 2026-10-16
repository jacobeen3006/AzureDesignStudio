namespace AzureDesignStudio.Discovery.Security;

/// <summary>One ARM management lock as the locks API returned it: the lock resource's own
/// ARM id, name, level (`CanNotDelete` / `ReadOnly` / anything the API carries) and notes.</summary>
public sealed record LockRow(string LockId, string Name, string Level, string? Notes);

/// <summary>The locks that actually bite a resource: the union of everything bound to the
/// resource or any ancestor scope; <c>ScopeId</c> is the deepest one. Levels stay raw ARM
/// strings - an unrecognized level is surfaced, never dropped.</summary>
public sealed record EffectiveLock(string ScopeId, IReadOnlyList<string> Levels, string? Notes)
{
    public bool CanNotDelete => Levels.Contains("cannotdelete", LockRowComparer.Instance);

    public bool ReadOnly => Levels.Contains("readonly", LockRowComparer.Instance);

    /// <summary>Chip label: the levels on the effective scope, ARM spelling preserved.</summary>
    public string Label => string.Join(" + ", Levels);

    private sealed class LockRowComparer : IEqualityComparer<string>
    {
        public static readonly LockRowComparer Instance = new();

        public bool Equals(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(string s) => StringComparer.OrdinalIgnoreCase.GetHashCode(s);
    }
}

/// <summary>
/// Resource locks (v2 plan 80): management locks bind to a scope and apply to everything
/// under it, so the answer for a resource is the union of the locks on its scope chain -
/// never a same-name guess. Scope is recovered from the lock resource id by cutting the
/// `/providers/Microsoft.Authorization/locks/{name}` tail; a row without that tail is not
/// a lock this scan can place, and is dropped rather than guessed. ARM ids are
/// case-insensitive, so every match is.
/// </summary>
public sealed class LockMap
{
    private readonly List<(string Scope, LockRow Lock)> _entries;

    public LockMap(IReadOnlyList<LockRow> locks)
    {
        _entries = [];
        foreach (var lockRow in locks)
        {
            var scope = ScopeOf(lockRow.LockId);
            if (scope is not null) _entries.Add((scope, lockRow));
        }
        DroppedCount = locks.Count - _entries.Count;
        TotalLocks = locks.Count;
    }

    /// <summary>Lock rows whose id did not carry the locks segment - unplaceable, reported, never silent.</summary>
    public int DroppedCount { get; }

    public int TotalLocks { get; }

    /// <summary>Lock rows with a resolvable scope, in fetch order.</summary>
    public IReadOnlyList<LockRow> Placed => _entries.Select(e => e.Lock).ToList();

    public int PlacedCount => _entries.Count;

    /// <summary>ARM enforces every lock bound to the resource or to any ancestor scope, so
    /// the answer is the union of all of them; the reported scope is the deepest one (what
    /// an owner sees in the portal). Null when nothing locks the resource.</summary>
    public EffectiveLock? For(string resourceId)
    {
        var applying = _entries.Where(e => Applies(e.Scope, resourceId)).ToList();
        if (applying.Count == 0) return null;
        var deepest = applying.OrderByDescending(e => e.Scope.Length).First().Scope;
        var levels = applying
            .Select(e => e.Lock.Level.Length == 0 ? "NotSpecified" : e.Lock.Level)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();
        var notes = applying.Select(e => e.Lock.Notes)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        return new EffectiveLock(deepest, levels, notes);
    }

    private static bool Applies(string scope, string resourceId) =>
        string.Equals(scope, resourceId, StringComparison.OrdinalIgnoreCase)
        || resourceId.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Scope a lock binds to: everything before its locks provider segment, case-insensitively.</summary>
    public static string? ScopeOf(string lockId)
    {
        const string marker = "/providers/microsoft.authorization/locks/";
        var idx = lockId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx <= 0 ? null : lockId[..idx];
    }
}

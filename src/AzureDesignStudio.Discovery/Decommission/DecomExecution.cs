using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AzureDesignStudio.Discovery.Decommission;

/// <summary>
/// Canonical primitives for decommission-window execution (v2 plan 13.5). A window lock is not a
/// deployment, so the what-if gate cannot bind it; the execution hash binds the exact lock set
/// instead (window + plan hash + subscription + purge date + sorted scopes). Everything is
/// deterministic from the window id - lock name, URI and notes - so release matches locks by
/// name only and a foreign lock survives by construction. The app only ever LANDS and RELEASES
/// window-owned locks; purge never executes here.
/// </summary>
public static class DecomExecution
{
    /// <summary>
    /// sha256: hash over the canonical lock-set identity. Scope order is normalized (trim +
    /// ordinal-by-lowercase sort), the subscription is trimmed + lowercased, and the purge date is
    /// UTC "O" - so the hash is order-invariant and byte-stable across callers.
    /// </summary>
    public static string ExecutionHash(string windowId, string planHash, string subscriptionId,
        DateTimeOffset purgeNotBefore, IEnumerable<string> scopes)
    {
        var canonical = new StringBuilder()
            .Append("decom-exec\n")
            .Append("windowId=").Append(windowId).Append('\n')
            .Append("planHash=").Append(planHash).Append('\n')
            .Append("sub=").Append(subscriptionId.Trim().ToLowerInvariant()).Append('\n')
            .Append("purge=").Append(purgeNotBefore.ToUniversalTime().ToString("O")).Append('\n')
            .Append("scopes=")
            .Append(string.Join("\n", scopes.Select(s => s.Trim()).OrderBy(s => s.ToLowerInvariant(), StringComparer.Ordinal)))
            .ToString();
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>
    /// The lock notes the runbook renders (stage 0) and the server writes to ARM - one source,
    /// byte-identical. The window bounds are UTC "O" so the notes round-trip exactly.
    /// </summary>
    public static string WindowNotes(string windowId, DateTimeOffset windowStart, DateTimeOffset windowEnd)
        => $"Azure Design Studio decommission window {windowStart.ToUniversalTime():O} .. {windowEnd.ToUniversalTime():O} (window '{windowId}')";

    /// <summary>
    /// ARM lock resource URI for a scope + lock name (api-version 2016-09-01). The last path
    /// segment IS the lock's name in ARM - there is no separate id - so the window's lock must be
    /// written and deleted under <see cref="ProtectionWindowPlanner.LockNameFor(string)"/>. (A
    /// lock PUT under any other segment, such as a guid, is listed with that segment as its name:
    /// the window then never recognises it as its own and can neither skip nor release it.) The
    /// caller passes the management endpoint with a trailing slash so the scope joins cleanly.
    /// </summary>
    public static string LockUri(string armEndpoint, string scope, string lockName)
        => $"{armEndpoint}{scope.TrimStart('/')}/providers/Microsoft.Authorization/locks/{Uri.EscapeDataString(lockName)}?api-version=2016-09-01";

    /// <summary>
    /// A window scope is spliced into ARM URLs that carry the linked subscription's credential, so
    /// it must be a plain ARM id at or under that subscription: no query or fragment, no relative
    /// or empty segments, no trailing slash. Anything else is refused before any request is built -
    /// and before the guardrail re-judgement, which only reads that one subscription.
    /// </summary>
    public static bool IsScopeInSubscription(string? scope, string subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(subscriptionId))
            return false;

        var id = scope.Trim();
        var root = "/subscriptions/" + subscriptionId.Trim();
        if (!id.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !id.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            return false;

        if (id.EndsWith('/') || id.Contains("//", StringComparison.Ordinal))
            return false;

        foreach (var ch in id)
            if (char.IsControl(ch) || ch is '?' or '#' or '\\' or '%')
                return false;

        return !id.Split('/').Any(segment => segment is "." or "..");
    }

    /// <summary>ARM lock body: CanNotDelete level with the window notes (JSON-escaped).</summary>
    public static string LockBody(string notes)
        => "{\"properties\":{\"level\":\"CanNotDelete\",\"notes\":" + JsonSerializer.Serialize(notes) + "}}";

    /// <summary>
    /// Parses an ARM lock list (a <c>value</c> array of <c>{name, id, ...}</c>) against the window's
    /// lock name. <see cref="Ours"/> is true when any item's name equals the lock name (ordinal);
    /// <see cref="ForeignNames"/> is every other lock's name - the ones a release must leave in place.
    /// An empty or missing value array yields no-ours, no-foreign.
    /// </summary>
    public static (bool Ours, IReadOnlyList<string> ForeignNames) ParseLockState(string listJson, string lockName)
    {
        var foreign = new List<string>();
        var ours = false;

        using var doc = JsonDocument.Parse(listJson);
        if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? ""
                    : "";
                if (string.Equals(name, lockName, StringComparison.Ordinal))
                    ours = true;
                else if (name.Length > 0)
                    foreign.Add(name);
            }
        }

        return (ours, foreign);
    }
}

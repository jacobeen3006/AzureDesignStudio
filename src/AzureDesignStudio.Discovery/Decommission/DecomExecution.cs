using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AzureDesignStudio.Discovery.Decommission;

/// <summary>
/// Canonical primitives for decommission-window execution (v2 plan 13.5). A window lock is not a
/// deployment, so the what-if gate cannot bind it; the execution hash binds the exact lock set
/// instead (window + plan hash + subscription + purge date + sorted scopes). Everything is
/// deterministic from the window id - lock name, id, URI and notes - so release matches locks by
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

    /// <summary>Deterministic lock id (guid) for a window + scope; the same pair always yields the same id.</summary>
    public static Guid LockIdFor(string windowId, string scope)
        => new(MD5.HashData(Encoding.UTF8.GetBytes(windowId + "\n" + scope.Trim().ToLowerInvariant())));

    /// <summary>
    /// ARM lock resource URI for a scope + lock id (api-version 2016-09-01). The caller passes the
    /// management endpoint with a trailing slash so the scope (leading slash trimmed) joins cleanly.
    /// </summary>
    public static string LockUri(string armEndpoint, string scope, Guid lockId)
        => $"{armEndpoint}{scope.TrimStart('/')}/providers/Microsoft.Authorization/locks/{lockId}?api-version=2016-09-01";

    /// <summary>ARM lock body: CanNotDelete level with the window notes (JSON-escaped).</summary>
    public static string LockBody(string notes)
        => "{\"properties\":{\"level\":\"CanNotDelete\",\"notes\":" + JsonSerializer.Serialize(notes) + "}}";

    /// <summary>
    /// Parses an ARM lock list (a <c>value</c> array of <c>{name, id, ...}</c>) against the window's
    /// lock name. <see cref="Ours"/> is true when any item's name equals the lock name (ordinal);
    /// <see cref="OurLockId"/> is that item's lock id (guid, from the id field);
    /// <see cref="ForeignNames"/> is every other lock's name - the ones a release must leave in place.
    /// An empty or missing value array yields no-ours, no-foreign, no-id.
    /// </summary>
    public static (bool Ours, IReadOnlyList<string> ForeignNames, Guid? OurLockId) ParseLockState(string listJson, string lockName)
    {
        var foreign = new List<string>();
        var ours = false;
        Guid? ourId = null;

        using var doc = JsonDocument.Parse(listJson);
        if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? ""
                    : "";
                if (string.Equals(name, lockName, StringComparison.Ordinal))
                {
                    ours = true;
                    if (ourId is null && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        var idStr = id.GetString() ?? "";
                        var lastSlash = idStr.LastIndexOf('/');
                        if (lastSlash >= 0 && Guid.TryParse(idStr[(lastSlash + 1)..], out var g))
                            ourId = g;
                    }
                }
                else if (name.Length > 0)
                    foreign.Add(name);
            }
        }

        return (ours, foreign, ourId);
    }
}

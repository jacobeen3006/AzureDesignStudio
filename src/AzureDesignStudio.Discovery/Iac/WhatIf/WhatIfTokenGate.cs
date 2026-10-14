using System.Security.Cryptography;
using System.Text;

namespace AzureDesignStudio.Discovery.Iac.WhatIf;

public enum WhatIfTokenStatus
{
    Ok,
    Missing,
    Stale,
    PayloadChanged,
    HasDelete,
}

public sealed record WhatIfTokenCheck(WhatIfTokenStatus Status, string? Detail, bool HasDelete = false, IReadOnlyList<string>? DeleteResourceIds = null)
{
    public bool IsOk => Status == WhatIfTokenStatus.Ok;
    public IReadOnlyList<string> Deletes => DeleteResourceIds ?? [];

    /// <summary>Stable machine-readable code the client can switch on (never localized).</summary>
    public string Code => Status switch
    {
        WhatIfTokenStatus.Missing => "WHAT_IF_REQUIRED",
        WhatIfTokenStatus.Stale => "WHAT_IF_STALE",
        WhatIfTokenStatus.PayloadChanged => "WHAT_IF_PAYLOAD_CHANGED",
        WhatIfTokenStatus.HasDelete => "WHAT_IF_HAS_DELETE",
        _ => "OK",
    };
}

/// <summary>
/// Makes what-if a mandatory, fresh preflight for deployment: every WhatIf call issues a
/// single-use token bound to the exact (subscription, resource group, template, parameters)
/// payload; CreateDeployment consumes it and rejects anything missing, expired, reissued,
/// or edited after the review. A prediction containing Delete - or one ARM could not fully
/// classify (Bad/Unknown rows, non-Succeeded status) - is refused outright: indeterminate
/// blast radius is never deployable in this phase.
/// </summary>
public sealed class WhatIfTokenGate(TimeSpan ttl, Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _tokens = new(StringComparer.Ordinal);
    private DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();

    private sealed record Entry(string PayloadHash, DateTimeOffset Expires, bool HasDelete,
        IReadOnlyList<string> DeleteResourceIds, string CanonicalPlanJson, string PlanHash, string? RevertPlanJson);

    /// <summary>
    /// Record a completed what-if review; returns the token the later deploy must present.
    /// The revert plan (pre-deploy snapshot + undo entries) rides the same live-preview record
    /// so approval persists exactly what the preview observed, while the plan hash keeps its
    /// scope + template + parameters identity.
    /// </summary>
    public string Issue(string subscriptionId, string resourceGroupName, string armTemplate, string parameters,
        WhatIfSummary summary, string canonicalPlanJson, string planHash, string? revertPlanJson = null)
    {
        // Fail closed: an indeterminate prediction never earns a deploy token, no matter
        // what the caller's checks looked like. This is the single choke point.
        if (summary.IndeterminacyReason is { } reason)
            throw new WhatIfException("Indeterminate", $"cannot issue a review token: {reason}");

        var token = Guid.NewGuid().ToString("N");
        var entry = new Entry(Hash(subscriptionId, resourceGroupName, armTemplate, parameters),
            Now.Add(ttl), summary.HasDelete, summary.DeleteResourceIds, canonicalPlanJson, planHash, revertPlanJson);

        lock (_sync)
        {
            _tokens[token] = entry;
            foreach (var key in _tokens.Where(kv => kv.Value.Expires <= Now).Select(kv => kv.Key).ToList())
                _tokens.Remove(key);
        }

        return token;
    }

    /// <summary>
    /// Approved-plan flow: a plan hash is approvable only while its what-if preview is live.
    /// Non-consuming peek by plan hash; expired previews are invisible here too.
    /// </summary>
    public bool TryPeekPlan(string planHash, out string canonicalPlanJson) =>
        TryPeekPlan(planHash, out canonicalPlanJson, out _);

    /// <summary>Peek that also yields the revert plan document recorded with the preview (null if none).</summary>
    public bool TryPeekPlan(string planHash, out string canonicalPlanJson, out string? revertPlanJson)
    {
        lock (_sync)
        {
            var hit = _tokens.Values.FirstOrDefault(e => e.Expires > Now
                && string.Equals(e.PlanHash, planHash, StringComparison.OrdinalIgnoreCase));
            canonicalPlanJson = hit?.CanonicalPlanJson ?? string.Empty;
            revertPlanJson = hit?.RevertPlanJson;
            return hit is not null;
        }
    }

    /// <summary>Validate and consume. A failed check never burns the token; a passed one is single-use.</summary>
    public WhatIfTokenCheck ValidateAndConsume(string? token, string subscriptionId, string resourceGroupName,
        string armTemplate, string parameters)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new WhatIfTokenCheck(WhatIfTokenStatus.Missing, "Run What-If on this exact template and parameters before deploying.");

        lock (_sync)
        {
            if (!_tokens.TryGetValue(token, out var entry))
                return new WhatIfTokenCheck(WhatIfTokenStatus.Stale, "What-If token is unknown or already used; run What-If again.");

            if (entry.Expires <= Now)
            {
                _tokens.Remove(token);
                return new WhatIfTokenCheck(WhatIfTokenStatus.Stale, $"What-If review expired after {(int)ttl.TotalMinutes} minutes; run What-If again.");
            }

            if (!string.Equals(entry.PayloadHash, Hash(subscriptionId, resourceGroupName, armTemplate, parameters), StringComparison.OrdinalIgnoreCase))
                return new WhatIfTokenCheck(WhatIfTokenStatus.PayloadChanged, "The template, parameters, or target changed after the What-If review; run What-If again.");
            if (entry.HasDelete)
                return new WhatIfTokenCheck(WhatIfTokenStatus.HasDelete,
                    "What-If predicts delete-bearing changes, which this version never deploys: " + string.Join(", ", entry.DeleteResourceIds),
                    HasDelete: true, DeleteResourceIds: entry.DeleteResourceIds);

            _tokens.Remove(token);
            return new WhatIfTokenCheck(WhatIfTokenStatus.Ok, null);
        }
    }

    private static string Hash(string subscriptionId, string resourceGroupName, string armTemplate, string parameters)
    {
        var payload = $"{subscriptionId}\n{resourceGroupName}\n{armTemplate}\n{parameters}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}

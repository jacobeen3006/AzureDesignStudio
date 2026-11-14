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
    GuardrailProtected,
}

public sealed record WhatIfTokenCheck(WhatIfTokenStatus Status, string? Detail, bool HasDelete = false,
    IReadOnlyList<string>? DeleteResourceIds = null, IReadOnlyList<Unmanaged.GuardrailTarget>? Targets = null)
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
        WhatIfTokenStatus.GuardrailProtected => "GUARDRAIL_PROTECTED",
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
    private long _sequence;
    private DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();

    private sealed record Entry(string PayloadHash, DateTimeOffset Expires, bool HasDelete,
        IReadOnlyList<string> DeleteResourceIds, string CanonicalPlanJson, string PlanHash, string? RevertPlanJson,
        IReadOnlyList<Unmanaged.GuardrailTarget>? GuardrailTargets, bool IsStack = false,
        string? RequestedByUserId = null, Guid? ScopeId = null, long Sequence = 0);

    /// <summary>
    /// Record a completed what-if review; returns the token the later deploy must present.
    /// The revert plan (pre-deploy snapshot + undo entries) rides the same live-preview record
    /// so approval persists exactly what the preview observed, while the plan hash keeps its
    /// scope + template + parameters identity.
    /// </summary>
    public string Issue(string subscriptionId, string resourceGroupName, string armTemplate, string parameters,
        WhatIfSummary summary, string canonicalPlanJson, string planHash, string? revertPlanJson = null,
        IReadOnlyList<Unmanaged.GuardrailTarget>? guardrailTargets = null,
        string? requestedByUserId = null, Guid? scopeId = null) =>
        Issue(DeployTargetScope.ResourceGroupScope(subscriptionId, resourceGroupName), armTemplate, parameters,
            summary, canonicalPlanJson, planHash, revertPlanJson, guardrailTargets, requestedByUserId, scopeId);

    /// <summary>Token bound to a full target identity (v2 11.2): the key names the canonical scope
    /// path, so a token issued for one scope can never authorize the same bytes at another.
    /// <paramref name="requestedByUserId"/> and <paramref name="scopeId"/> record who asked and which
    /// tenant scope (the caller's, D-07) the preview belongs to; the scope-aware peek and consume only
    /// serve that scope.</summary>
    public string Issue(DeployTargetScope scope, string armTemplate, string parameters,
        WhatIfSummary summary, string canonicalPlanJson, string planHash, string? revertPlanJson = null,
        IReadOnlyList<Unmanaged.GuardrailTarget>? guardrailTargets = null,
        string? requestedByUserId = null, Guid? scopeId = null)
    {
        // Fail closed: an indeterminate prediction never earns a deploy token, no matter
        // what the caller's checks looked like. This is the single choke point.
        if (summary.IndeterminacyReason is { } reason)
            throw new WhatIfException("Indeterminate", $"cannot issue a review token: {reason}");

        var token = Guid.NewGuid().ToString("N");
        var entry = new Entry(Hash(scope, armTemplate, parameters),
            Now.Add(ttl), summary.HasDelete, summary.DeleteResourceIds, canonicalPlanJson, planHash, revertPlanJson,
            guardrailTargets, RequestedByUserId: requestedByUserId, ScopeId: scopeId);

        Store(token, entry);
        return token;
    }

    private void Store(string token, Entry entry)
    {
        lock (_sync)
        {
            _tokens[token] = entry with { Sequence = ++_sequence };
            foreach (var key in _tokens.Where(kv => kv.Value.Expires <= Now).Select(kv => kv.Key).ToList())
                _tokens.Remove(key);
        }
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
            // A deployment preview wins over a stack preview of the same plan: it carries the revert plan.
            var hit = _tokens.Values.Where(e => e.Expires > Now
                    && string.Equals(e.PlanHash, planHash, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.IsStack ? 1 : 0).FirstOrDefault();
            canonicalPlanJson = hit?.CanonicalPlanJson ?? string.Empty;
            revertPlanJson = hit?.RevertPlanJson;
            return hit is not null;
        }
    }

    /// <summary>
    /// Scope-aware peek (D-07): only live previews issued for <paramref name="scopeId"/> are considered
    /// (a preview issued without a scope is invisible here). A deployment preview wins over a stack
    /// preview of the same plan, then the newest wins. Also yields the requester recorded with the
    /// winning preview.
    /// </summary>
    public bool TryPeekPlan(string planHash, Guid scopeId, out string canonicalPlanJson,
        out string? revertPlanJson, out string? requestedByUserId)
    {
        lock (_sync)
        {
            var hit = _tokens.Values.Where(e => e.Expires > Now && e.ScopeId == scopeId
                    && string.Equals(e.PlanHash, planHash, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.IsStack ? 1 : 0).ThenByDescending(e => e.Sequence).FirstOrDefault();
            canonicalPlanJson = hit?.CanonicalPlanJson ?? string.Empty;
            revertPlanJson = hit?.RevertPlanJson;
            requestedByUserId = hit?.RequestedByUserId;
            return hit is not null;
        }
    }

    /// <summary>
    /// Validate and consume. A failed check never burns the token; a passed one is single-use.
    /// The optional guardrail veto runs on the targets bound at issue time - a vetoed plan is
    /// refused without spending the token, exactly like the read-only approval checks upstream.
    /// </summary>
    public WhatIfTokenCheck ValidateAndConsume(string? token, string subscriptionId, string resourceGroupName,
        string armTemplate, string parameters, Func<IReadOnlyList<Unmanaged.GuardrailTarget>, string?>? guardrailVeto = null,
        Guid? scopeId = null) =>
        ValidateAndConsume(token, DeployTargetScope.ResourceGroupScope(subscriptionId, resourceGroupName),
            armTemplate, parameters, guardrailVeto, scopeId);

    /// <param name="scopeId">When given, the token must have been issued for that scope; any other
    /// answers the same Stale result as an unknown token and is left alone.</param>
    public WhatIfTokenCheck ValidateAndConsume(string? token, DeployTargetScope scope,
        string armTemplate, string parameters, Func<IReadOnlyList<Unmanaged.GuardrailTarget>, string?>? guardrailVeto = null,
        Guid? scopeId = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new WhatIfTokenCheck(WhatIfTokenStatus.Missing, "Run What-If on this exact template and parameters before deploying.");

        lock (_sync)
        {
            if (!_tokens.TryGetValue(token, out var entry) || (scopeId is { } caller && entry.ScopeId != caller))
                return new WhatIfTokenCheck(WhatIfTokenStatus.Stale, "What-If token is unknown or already used; run What-If again.");

            if (entry.Expires <= Now)
            {
                _tokens.Remove(token);
                return new WhatIfTokenCheck(WhatIfTokenStatus.Stale, $"What-If review expired after {(int)ttl.TotalMinutes} minutes; run What-If again.");
            }

            if (entry.IsStack)
                return new WhatIfTokenCheck(WhatIfTokenStatus.PayloadChanged, "This review token belongs to a deployment-stack what-if, not a plain deployment; run What-If again.");

            if (!string.Equals(entry.PayloadHash, Hash(scope, armTemplate, parameters), StringComparison.OrdinalIgnoreCase))
                return new WhatIfTokenCheck(WhatIfTokenStatus.PayloadChanged, "The template, parameters, or target changed after the What-If review; run What-If again.");
            if (entry.HasDelete)
                return new WhatIfTokenCheck(WhatIfTokenStatus.HasDelete,
                    "What-If predicts delete-bearing changes, which this version never deploys: " + string.Join(", ", entry.DeleteResourceIds),
                    HasDelete: true, DeleteResourceIds: entry.DeleteResourceIds);

            var targets = entry.GuardrailTargets ?? [];
            if (guardrailVeto is not null && guardrailVeto(targets) is { } vetoDetail)
                return new WhatIfTokenCheck(WhatIfTokenStatus.GuardrailProtected, vetoDetail, Targets: targets);

            _tokens.Remove(token);
            return new WhatIfTokenCheck(WhatIfTokenStatus.Ok, null, Targets: targets);
        }
    }

    /// <summary>
    /// Record a completed deployment-stacks what-if. A stack token is a different kind from a
    /// deployment token: it binds the exact stack body (<paramref name="stackPlanHash"/> =
    /// <c>DeploymentStackBody.PlanHash</c>: template, parameters, policy, bypass flag, stack name)
    /// under the resource group, and only <see cref="ValidateAndConsumeForStack"/> accepts it.
    /// <paramref name="approvalPlanHash"/> and <paramref name="canonicalPlanJson"/> make the
    /// scope+template+parameters plan hash approvable (<see cref="TryPeekPlan(string, out string)"/>).
    /// The caller decides whether an indeterminate result earns a token; this method does not see the summary.
    /// </summary>
    public string IssueForStack(string subscriptionId, string resourceGroupName, string stackPlanHash,
        bool hasDelete, IReadOnlyList<string> deleteIds, string? approvalPlanHash = null,
        string? canonicalPlanJson = null, IReadOnlyList<Unmanaged.GuardrailTarget>? guardrailTargets = null,
        string? requestedByUserId = null, Guid? scopeId = null)
    {
        var token = Guid.NewGuid().ToString("N");
        var entry = new Entry(StackHash(subscriptionId, resourceGroupName, stackPlanHash), Now.Add(ttl), hasDelete,
            deleteIds, canonicalPlanJson ?? string.Empty, approvalPlanHash ?? string.Empty, null, guardrailTargets, IsStack: true,
            RequestedByUserId: requestedByUserId, ScopeId: scopeId);

        Store(token, entry);
        return token;
    }

    /// <summary>Validate and consume a stack token: same rules as the deployment overload (a failed
    /// check never burns it, a passed one is single-use, delete refuses, the guardrail veto runs on the
    /// bound targets), but a deployment token is refused with <see cref="WhatIfTokenStatus.PayloadChanged"/>.</summary>
    public WhatIfTokenCheck ValidateAndConsumeForStack(string? token, string subscriptionId, string resourceGroupName,
        string stackPlanHash, Func<IReadOnlyList<Unmanaged.GuardrailTarget>, string?>? guardrailVeto = null,
        Guid? scopeId = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new WhatIfTokenCheck(WhatIfTokenStatus.Missing, "Run the stack What-If on this exact template, parameters and stack policy before deploying.");

        lock (_sync)
        {
            if (!_tokens.TryGetValue(token, out var entry) || (scopeId is { } caller && entry.ScopeId != caller))
                return new WhatIfTokenCheck(WhatIfTokenStatus.Stale, "What-If token is unknown or already used; run What-If again.");

            if (entry.Expires <= Now)
            {
                _tokens.Remove(token);
                return new WhatIfTokenCheck(WhatIfTokenStatus.Stale, $"What-If review expired after {(int)ttl.TotalMinutes} minutes; run What-If again.");
            }

            if (!entry.IsStack)
                return new WhatIfTokenCheck(WhatIfTokenStatus.PayloadChanged, "This review token belongs to a plain deployment what-if; a stack deployment needs the stack What-If.");
            if (!string.Equals(entry.PayloadHash, StackHash(subscriptionId, resourceGroupName, stackPlanHash), StringComparison.OrdinalIgnoreCase))
                return new WhatIfTokenCheck(WhatIfTokenStatus.PayloadChanged, "The template, parameters, stack policy or stack name changed after the What-If review; run What-If again.");
            if (entry.HasDelete)
                return new WhatIfTokenCheck(WhatIfTokenStatus.HasDelete,
                    "Stack What-If predicts delete-bearing changes, which this version never deploys: " + string.Join(", ", entry.DeleteResourceIds),
                    HasDelete: true, DeleteResourceIds: entry.DeleteResourceIds);

            var targets = entry.GuardrailTargets ?? [];
            if (guardrailVeto is not null && guardrailVeto(targets) is { } vetoDetail)
                return new WhatIfTokenCheck(WhatIfTokenStatus.GuardrailProtected, vetoDetail, Targets: targets);

            _tokens.Remove(token);
            return new WhatIfTokenCheck(WhatIfTokenStatus.Ok, null, Targets: targets);
        }
    }

    private static string StackHash(string subscriptionId, string resourceGroupName, string stackPlanHash)
    {
        var payload = $"stack\n{DeployTargetScope.ResourceGroupScope(subscriptionId, resourceGroupName).ScopePath}\n{stackPlanHash}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string Hash(DeployTargetScope scope, string armTemplate, string parameters)
    {
        var payload = $"{scope.ScopePath}\n{armTemplate}\n{parameters}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}

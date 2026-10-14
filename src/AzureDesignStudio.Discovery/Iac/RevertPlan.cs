using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzureDesignStudio.Discovery.Iac.WhatIf;

namespace AzureDesignStudio.Discovery.Iac;

public enum RevertActionKind
{
    /// <summary>Apply created this resource; undo removes it (execution blocked until Phase 13).</summary>
    Delete,

    /// <summary>Apply modified this resource; undo re-deploys the captured pre-deployment payload.</summary>
    Restore,

    /// <summary>Apply deleted this resource; undo recreates it from the captured pre-deployment payload.</summary>
    Recreate,

    /// <summary>Honest gap: the pre-deployment state cannot be reconstructed, so this row is never revertible.</summary>
    Unrevertable,
}

/// <summary>One undo step derived from a what-if prediction row.</summary>
public sealed record RevertEntry(RevertActionKind Action, string? ResourceId, string? PayloadJson, string? Reason);

/// <summary>
/// Pre-deploy snapshot plus inverse plan (v2 plan 11.4/[57]), built from the FULL (untruncated)
/// what-if before payloads: what existed before the apply, and one typed undo entry per predicted
/// change. The document is canonical by construction (fixed member order, entries sorted by the
/// serialized action string then resource id, both ordinal) so its <see cref="Hash"/> is stable. Execution is deliberately never enabled here:
/// revert entries carry delete-bearing operations, and the deletion safety gate keeps them inert
/// until Phase 13. The plan is stored with the approval and referenced by hash on every apply
/// audit event; it is not part of the plan-hash identity (scope + template + parameters only).
/// </summary>
public sealed class RevertPlan
{
    public const string HashPrefix = "sha256:";

    /// <summary>Why nothing here executes, surfaced verbatim in the document.</summary>
    public const string ExecutableNote =
        "Revert execution is disabled until Phase 13 (guard-protected deprecation): the revert contains delete operations.";

    private RevertPlan(string documentJson, string hash, IReadOnlyList<RevertEntry> entries, int unrevertableCount)
    {
        DocumentJson = documentJson;
        Hash = hash;
        Entries = entries;
        UnrevertableCount = unrevertableCount;
    }

    /// <summary>Canonical revert plan document; the durable snapshot artifact.</summary>
    public string DocumentJson { get; }

    /// <summary>sha256:, lowercase hex, over the UTF-8 bytes of <see cref="DocumentJson"/>.</summary>
    public string Hash { get; }

    public IReadOnlyList<RevertEntry> Entries { get; }

    public int UnrevertableCount { get; }

    /// <summary>
    /// Derives the undo plan from a what-if summary. <paramref name="coverageNote"/> discloses
    /// preview blind spots (staged applies preview only one batch) inside the artifact itself.
    /// </summary>
    public static RevertPlan Build(WhatIfSummary summary, string? coverageNote = null)
    {
        var entries = new List<RevertEntry>();
        var unrevertable = 0;

        foreach (var change in summary.Changes)
        {
            // No undo can name a resource ARM never identified - true for every non-skipped kind.
            if (change.Kind is not (WhatIfChangeKind.NoChange or WhatIfChangeKind.Ignore)
                && string.IsNullOrWhiteSpace(change.ResourceId))
            {
                Add(entries, ref unrevertable, RevertActionKind.Unrevertable, change, payload: null, reason: "the resource ID is unknown");
                continue;
            }

            switch (change.Kind)
            {
                case WhatIfChangeKind.NoChange:
                case WhatIfChangeKind.Ignore:
                    continue;

                case WhatIfChangeKind.Create:
                    Add(entries, ref unrevertable, RevertActionKind.Delete, change, payload: null, reason: null);
                    continue;

                case WhatIfChangeKind.Modify:
                case WhatIfChangeKind.Delete:
                {
                    var action = change.Kind == WhatIfChangeKind.Modify ? RevertActionKind.Restore : RevertActionKind.Recreate;
                    if (string.IsNullOrWhiteSpace(change.BeforeJson))
                    {
                        Add(entries, ref unrevertable, RevertActionKind.Unrevertable, change, payload: null, reason: "ARM did not return the pre-change payload");
                        continue;
                    }

                    // Any valid JSON is a snapshot (objects, arrays, scalars, JSON null included)
                    // and embeds as-is; only malformed JSON is unrevertable. JsonNode.Parse maps
                    // JSON null to a null node, so the null payload is preserved explicitly.
                    string? payload;
                    try
                    {
                        payload = JsonNode.Parse(change.BeforeJson)?.ToJsonString() ?? "null";
                    }
                    catch (JsonException)
                    {
                        Add(entries, ref unrevertable, RevertActionKind.Unrevertable, change, payload: null, reason: "failed to parse pre-deployment payload");
                        continue;
                    }

                    Add(entries, ref unrevertable, action, change, payload: payload, reason: null);
                    continue;
                }

                default:
                    // Bad/Unknown: ARM's prediction was indeterminate, so the undo is too.
                    Add(entries, ref unrevertable, RevertActionKind.Unrevertable, change, payload: null, reason: "ARM could not classify the predicted change");
                    continue;
            }
        }

        var ordered = entries
            .OrderBy(e => e.Action.ToString().ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(e => e.ResourceId ?? "", StringComparer.Ordinal)
            .ToList();

        var document = new JsonObject
        {
            ["version"] = 1,
            ["coverageNote"] = coverageNote,
            ["executable"] = false,
            ["executableNote"] = ExecutableNote,
            ["entries"] = new JsonArray(ordered
                .Select<RevertEntry, JsonNode?>(e => new JsonObject
                {
                    ["action"] = e.Action.ToString().ToLowerInvariant(),
                    ["resourceId"] = e.ResourceId,
                    ["payload"] = e.PayloadJson is null ? null : JsonNode.Parse(e.PayloadJson),
                    ["reason"] = e.Reason,
                })
                .ToArray()),
            ["unrevertableCount"] = unrevertable,
        };

        var json = document.ToJsonString();
        return new RevertPlan(json, HashOf(json)!, ordered, unrevertable);
    }

    /// <summary>Hash of a stored revert document; null in, null out (no revert, no claim).</summary>
    public static string? HashOf(string? documentJson) =>
        string.IsNullOrEmpty(documentJson)
            ? null
            : HashPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(documentJson))).ToLowerInvariant();

    private static void Add(List<RevertEntry> entries, ref int unrevertable, RevertActionKind action,
        WhatIfChange change, string? payload, string? reason)
    {
        if (action == RevertActionKind.Unrevertable)
        {
            unrevertable++;
        }

        entries.Add(new RevertEntry(action,
            string.IsNullOrWhiteSpace(change.ResourceId) ? null : change.ResourceId, payload, reason));
    }
}

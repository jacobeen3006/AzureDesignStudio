using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AzureDesignStudio.Core.Common
{
    /// <summary>Which ARM deployment scope a plan is bound to; the plan hash names it, so an
    /// approval for a resource group can never authorize the same bytes at another scope (v2 11.2).</summary>
    public enum DeployPlanScopeKind
    {
        ResourceGroup = 0,
        Subscription = 1,
        ManagementGroup = 2,
        Tenant = 3,
    }

    /// <summary>
    /// Canonical, hash-stamped deployment plan (v2 plan 11.3 pre-work for the approved-plan-hash
    /// gate [62]): plan identity is scope + template + parameters and nothing else. Canonical JSON
    /// is compact with object keys sorted ordinal, arrays kept in order and literal tokens verbatim -
    /// so semantically equal bodies always share one hash, while any change to what would be deployed
    /// changes the hash that approvals and audit records bind to. Storage and orchestration live in
    /// the Workspace + hosts; this is the single definition of what a plan hash means.
    /// </summary>
    public sealed class DeployPlanArtifact
    {
        /// <summary>Algorithm tag of <see cref="PlanHash"/>; a bare hex digest is never ambiguous.</summary>
        public const string HashPrefix = "sha256:";

        private DeployPlanArtifact(string canonicalJson, string planHash)
        {
            CanonicalJson = canonicalJson;
            PlanHash = planHash;
        }

        /// <summary>Compact canonical JSON: {"parameters":...,"scope":{...},"template":...}.</summary>
        public string CanonicalJson { get; }

        /// <summary>sha256:, lowercase hex, over the UTF-8 bytes of <see cref="CanonicalJson"/>.</summary>
        public string PlanHash { get; }

        /// <summary>
        /// Plan bound to a non-resource-group target (v2 plan 11.2): the canonical scope member
        /// names exactly the approved identity - management group id, subscription id, or nothing
        /// for tenant - so the hash is unique per target and never collides with a resource-group
        /// plan (whose member set keeps both ids and stays byte-identical to older approvals).
        /// </summary>
        public static DeployPlanArtifact Create(DeployPlanScopeKind kind, string? subscriptionId, string? resourceGroup,
            string? managementGroupId, string templateJson, string parametersJson = "{}")
        {
            var scopeMember = kind switch
            {
                DeployPlanScopeKind.ResourceGroup when !string.IsNullOrWhiteSpace(subscriptionId) && !string.IsNullOrWhiteSpace(resourceGroup) =>
                    "{\"resourceGroup\":" + JsonSerializer.Serialize(resourceGroup) + ",\"subscriptionId\":" + JsonSerializer.Serialize(subscriptionId) + "}",
                DeployPlanScopeKind.ResourceGroup => throw new ArgumentException("A plan scope needs a subscription id and a resource group.", nameof(subscriptionId)),
                DeployPlanScopeKind.Subscription when !string.IsNullOrWhiteSpace(subscriptionId) =>
                    "{\"subscriptionId\":" + JsonSerializer.Serialize(subscriptionId) + "}",
                DeployPlanScopeKind.Subscription => throw new ArgumentException("A subscription-scope plan needs a subscription id.", nameof(subscriptionId)),
                DeployPlanScopeKind.ManagementGroup when !string.IsNullOrWhiteSpace(managementGroupId) =>
                    "{\"managementGroupId\":" + JsonSerializer.Serialize(managementGroupId) + "}",
                DeployPlanScopeKind.ManagementGroup => throw new ArgumentException("A management-group-scope plan needs a management group id.", nameof(managementGroupId)),
                DeployPlanScopeKind.Tenant => "{}",
                _ => throw new ArgumentException($"Unknown plan scope kind '{kind}'.", nameof(kind)),
            };

            var template = Parse(templateJson, nameof(templateJson));
            var parameters = Parse(parametersJson, nameof(parametersJson));

            var sb = new StringBuilder();
            sb.Append("{\"parameters\":");
            AppendCanonical(sb, parameters);
            sb.Append(",\"scope\":").Append(scopeMember);
            sb.Append(",\"template\":");
            AppendCanonical(sb, template);
            sb.Append('}');

            var json = sb.ToString();
            return new DeployPlanArtifact(json, Hash(json));
        }

        public static DeployPlanArtifact Create(string subscriptionId, string resourceGroup, string templateJson, string parametersJson = "{}")
        {
            if (string.IsNullOrWhiteSpace(subscriptionId))
                throw new ArgumentException("A plan scope needs a subscription id.", nameof(subscriptionId));
            if (string.IsNullOrWhiteSpace(resourceGroup))
                throw new ArgumentException("A plan scope needs a resource group.", nameof(resourceGroup));

            // One canonical writer for every target shape; the resource-group member set is
            // byte-identical to every plan hash recorded before multi-scope targets existed.
            return Create(DeployPlanScopeKind.ResourceGroup, subscriptionId, resourceGroup, null, templateJson, parametersJson);
        }

        /// <summary>
        /// Recomputes the hash of a stored plan body (canonicalizing first, so a pretty-printed
        /// store copy still verifies). False means the body no longer matches what was approved -
        /// the gate refuses to execute on false, never on a best guess.
        /// </summary>
        public static bool Verify(string planJson, string expectedHash)
        {
            if (string.IsNullOrEmpty(expectedHash))
                return false;
            string recomputed;
            try
            {
                recomputed = Hash(Canonicalize(planJson));
            }
            catch (JsonException)
            {
                return false;
            }
            return string.Equals(recomputed, expectedHash, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Canonical form of any stored plan JSON; throws <see cref="JsonException"/> on non-JSON.</summary>
        public static string Canonicalize(string planJson)
        {
            using var doc = JsonDocument.Parse(planJson);
            var sb = new StringBuilder();
            AppendCanonical(sb, doc.RootElement);
            return sb.ToString();
        }

        private static JsonElement Parse(string json, string argName)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }
            catch (JsonException e)
            {
                throw new ArgumentException($"{argName} is not valid JSON.", argName, e);
            }
        }

        private static string Hash(string canonicalJson) =>
            HashPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();

        /// <summary>Compact canonical writer: keys sorted ordinal, arrays keep order, literal tokens verbatim.</summary>
        private static void AppendCanonical(StringBuilder sb, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    sb.Append('{');
                    var firstProperty = true;
                    foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        if (!firstProperty) sb.Append(',');
                        firstProperty = false;
                        sb.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                        AppendCanonical(sb, property.Value);
                    }
                    sb.Append('}');
                    break;
                case JsonValueKind.Array:
                    sb.Append('[');
                    var firstItem = true;
                    foreach (var child in element.EnumerateArray())
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        AppendCanonical(sb, child);
                    }
                    sb.Append(']');
                    break;
                default:
                    // Numbers keep their literal text: 1 and 1.0 hash apart rather than pretending equality.
                    sb.Append(element.GetRawText());
                    break;
            }
        }
    }
}

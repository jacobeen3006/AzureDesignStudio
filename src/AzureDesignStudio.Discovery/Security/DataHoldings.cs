using System.Text.Json;
using AzureDesignStudio.Discovery.Inventory;

namespace AzureDesignStudio.Discovery.Security;

/// <summary>What the scan evidence says about a flag: Yes/No are proven from ARM properties,
/// Unknown means the scan cannot see the answer (never guessed), NotApplicable means the
/// concept does not exist for this resource kind.</summary>
public enum DataEvidence { Yes, No, Unknown, NotApplicable }

/// <summary>One data-holding resource [88] and its three governance answers.</summary>
public sealed record DataHoldingRow(
    string Id,
    string Name,
    string Type,
    string ResourceGroup,
    string SubscriptionId,
    DataEvidence CmkEncryption,
    string? CmkDetail,
    DataEvidence PrivateOnly,
    string? PrivateDetail,
    DataEvidence SoftDelete,
    string? SoftDeleteDetail);

public sealed record DataHoldingsAudit(
    IReadOnlyList<DataHoldingRow> Rows,
    int TotalResources,
    IReadOnlyList<string> Caveats);

/// <summary>
/// [88] Data-holding inventory: which resources actually hold data (storage accounts, SQL/
/// Postgres/MySQL servers, Cosmos DB, Key Vault, Fabric capacities) and - strictly from what
/// the typeless scan captured - whether they use customer-managed keys, refuse public access,
/// and carry soft-delete protection. The honest answer is often Unknown: CMK keys and blob
/// soft-delete live on child resources the scan never pages, and those answers say so instead
/// of implying a false green.
/// </summary>
public static class DataHoldings
{
    private const string KeysCaveat =
        "keys-not-scanned: customer-managed keys and blob soft-delete live on child resources (keys, blobServices) that the typeless scan does not page - only flags carried on the resource itself are judged";
    private const string NetworkCaveat =
        "network-path-not-simulated: private-only reads ARM publicNetworkAccess/networkAcls on the resource; NSGs, UDRs and private DNS are not evaluated";
    private const string KeysUnknownDetail = "CMK rides keys child resources the scan never sees";
    private const string ChildSoftDeleteUnknown = "soft-delete lives on child resources the scan never sees";

    public static DataHoldingsAudit Analyze(IReadOnlyList<InventoryItem> items)
    {
        var rows = new List<DataHoldingRow>();
        foreach (var item in items)
        {
            var type = item.Type.ToLowerInvariant();
            var answer = Judge(type, item);
            if (answer is null) continue;
            var (cmk, cmkDetail, priv, privDetail, soft, softDetail) = answer.Value;
            rows.Add(new DataHoldingRow(item.Id, item.Name, item.Type, item.ResourceGroup, item.SubscriptionId,
                cmk, cmkDetail, priv, privDetail, soft, softDetail));
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return new DataHoldingsAudit(rows, items.Count, [KeysCaveat, NetworkCaveat]);
    }

    private static (DataEvidence, string?, DataEvidence, string?, DataEvidence, string?)? Judge(
        string type, InventoryItem item)
    {
        var raw = item.Raw;

        if (type == "microsoft.storage/storageaccounts")
        {
            var cmk = JudgeStorageCmk(raw, out var cmkDetail);
            var priv = JudgePublicNetwork(raw, preferNetworkAcls: true, out var privDetail);
            return (cmk, cmkDetail, priv, privDetail, DataEvidence.Unknown, ChildSoftDeleteUnknown);
        }
        if (type == "microsoft.sql/servers" || type.StartsWith("microsoft.dbforpostgresql/", StringComparison.Ordinal)
            || type.StartsWith("microsoft.dbformysql/", StringComparison.Ordinal))
        {
            var priv = JudgePublicNetwork(raw, preferNetworkAcls: false, out var privDetail);
            return (DataEvidence.Unknown, KeysUnknownDetail, priv, privDetail,
                DataEvidence.NotApplicable, "server backup is the baseline; retention policies are child resources");
        }
        if (type == "microsoft.documentdb/databaseaccounts")
        {
            var priv = JudgePublicNetwork(raw, preferNetworkAcls: false, out var privDetail);
            return (DataEvidence.Unknown, KeysUnknownDetail, priv, privDetail,
                DataEvidence.Unknown, "PITR/soft-delete are container-level child facts");
        }
        if (type == "microsoft.keyvault/vaults")
        {
            var priv = JudgePublicNetwork(raw, preferNetworkAcls: true, out var privDetail);
            var soft = DataEvidence.Unknown;
            string? softDetail = "no enableSoftDelete flag in the scan properties";
            if (raw is { } r && r.TryGetProperty("enableSoftDelete", out var sd))
            {
                var softOn = sd.ValueKind switch
                {
                    JsonValueKind.True => (bool?)true,
                    JsonValueKind.False => false,
                    _ => null,
                };
                if (softOn is { } on)
                {
                    soft = on ? DataEvidence.Yes : DataEvidence.No;
                    var purge = r.TryGetProperty("enablePurgeProtection", out var pp)
                        && pp.ValueKind == JsonValueKind.True;
                    softDetail = $"enableSoftDelete: {on}; purge protection {(purge ? "on" : "off or not reported")}";
                }
                else softDetail = $"enableSoftDelete carried a non-boolean value: {sd}";
            }
            return (DataEvidence.Unknown, KeysUnknownDetail, priv, privDetail, soft, softDetail);
        }
        if (type == "microsoft.fabric/capacities")
            return (DataEvidence.Unknown, KeysUnknownDetail, DataEvidence.NotApplicable, "capacity-level network settings are not per-resource ARM flags",
                DataEvidence.NotApplicable, null);

        return null; // not a data-holding family
    }

    private static DataEvidence JudgeStorageCmk(JsonElement? raw, out string? detail)
    {
        detail = null;
        if (raw is { } r && r.TryGetProperty("encryption", out var enc)
            && enc.TryGetProperty("keySource", out var ks))
        {
            var v = ks.ToString();
            detail = $"encryption.keySource: {v}";
            return string.Equals(v, "Microsoft.Keyvault", StringComparison.OrdinalIgnoreCase)
                ? DataEvidence.Yes : DataEvidence.No;
        }
        detail = "no encryption.keySource in the scan properties";
        return DataEvidence.Unknown;
    }

    private static DataEvidence JudgePublicNetwork(JsonElement? raw, bool preferNetworkAcls, out string? detail)
    {
        detail = null;
        if (raw is not { } r)
        {
            detail = "no properties in the scan row";
            return DataEvidence.Unknown;
        }
        // publicNetworkAccess is the authoritative switch; networkAcls.defaultAction only
        // answers when the switch is absent (older api-versions).
        if (r.TryGetProperty("publicNetworkAccess", out var pna))
        {
            var v = pna.ToString();
            if (string.Equals(v, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                detail = $"publicNetworkAccess: {v}";
                return DataEvidence.Yes;
            }
            if (string.Equals(v, "enabled", StringComparison.OrdinalIgnoreCase))
            {
                detail = $"publicNetworkAccess: {v}";
                return DataEvidence.No;
            }
            detail = $"publicNetworkAccess carried an unrecognized value: {v}";
            return DataEvidence.Unknown;
        }
        if (preferNetworkAcls && r.TryGetProperty("networkAcls", out var acl)
            && acl.TryGetProperty("defaultAction", out var act))
        {
            var v = act.ToString();
            detail = $"networkAcls.defaultAction: {v} (no publicNetworkAccess flag)";
            return string.Equals(v, "deny", StringComparison.OrdinalIgnoreCase) ? DataEvidence.Yes : DataEvidence.No;
        }
        detail = "neither publicNetworkAccess nor networkAcls.defaultAction in the scan properties";
        return DataEvidence.Unknown;
    }

}

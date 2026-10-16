using System.ComponentModel;
using System.Text.Json;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Mcp;
using AzureDesignStudio.Discovery.Unmanaged;
using AzureDesignStudio.Workspace;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AzureDesignStudio.Mcp;

/// <summary>
/// The 13 read-only tools of the local MCP server (v2 [93]): workspace truth (scans,
/// inventory, diffs, changes, cost cache, designs, exposure, reachability, unmanaged,
/// decommission planning, windows) over the desktop workspace database. No tool here
/// mutates the workspace or calls ARM; the decommission tool is PLAN-ONLY - the purge
/// never runs from here.
/// </summary>
[McpServerToolType]
public sealed class AdsMcpTools(AdsWorkspace workspace, ILogger<AdsMcpTools> logger)
{
    [McpServerTool(Name = "ads_scans_list", ReadOnly = true)]
    [Description("Every scan run stored in the local workspace, newest first: id, scope, status, resource count, start/finish, error. Read-only.")]
    public async Task<IReadOnlyList<ScanListView>> AdsScansList()
    {
        var scans = await workspace.ListScansAsync();
        return scans.Select(s => new ScanListView(
            s.Id, s.Scope, s.ScopeType, s.Status, s.ResourceCount, s.StartedAt, s.FinishedAt, s.Error)).ToList();
    }

    [McpServerTool(Name = "ads_scan_get", ReadOnly = true)]
    [Description("One scan run: the manifest JSON as stored plus a counts-by-type summary of its snapshot. Read-only.")]
    public async Task<ScanDetailView> AdsScanGet([Description("The stored scan id (exact).")] string scanId)
    {
        var scan = await RequireScanAsync(scanId);
        var items = ToItems(await workspace.GetSnapshotsAsync(scan.Id), out _);
        return new ScanDetailView(
            scan.Id, scan.Scope, scan.ScopeType, scan.Status, scan.ResourceCount,
            scan.StartedAt, scan.FinishedAt, scan.Error, scan.ManifestJson,
            AdsToolEngine.TypeSummary(items));
    }

    [McpServerTool(Name = "ads_scan_inventory", ReadOnly = true)]
    [Description("One page of a scan's inventory, filtered (type contains / scope prefix / tag key+value, case-insensitive). Malformed snapshot rows are counted and skipped, never fatal. Read-only.")]
    public async Task<ScanInventoryView> AdsScanInventory(
        [Description("The stored scan id (exact).")] string scanId,
        [Description("Case-insensitive substring of the resource type.")] string? typeContains = null,
        [Description("Case-insensitive prefix of the resource id (ARM scope).")] string? scopePrefix = null,
        [Description("Tag key to filter on (case-insensitive).")] string? tagKey = null,
        [Description("Tag value required when tagKey is set (case-insensitive).")] string? tagValue = null,
        [Description("Page size; 100 default, 1000 max.")] int limit = 100,
        [Description("Zero-based page offset.")] int offset = 0)
    {
        var scan = await RequireScanAsync(scanId);
        var items = ToItems(await workspace.GetSnapshotsAsync(scan.Id), out var malformed);
        var page = AdsToolEngine.InventoryQuery(
            items, new InventoryFilter(typeContains, scopePrefix, tagKey, tagValue),
            Math.Clamp(limit, 1, 1000), Math.Max(offset, 0));
        return new ScanInventoryView(page, items.Count, malformed,
            MalformedCaveats(malformed));
    }

    [McpServerTool(Name = "ads_scan_diff", ReadOnly = true)]
    [Description("Drift between two scans (added / changed / removed, volatile platform churn separated from drift). Read-only.")]
    public async Task<DiffSummary> AdsScanDiff(
        [Description("The earlier scan id (exact).")] string beforeScanId,
        [Description("The later scan id (exact).")] string afterScanId,
        [Description("How many changed resources to include; 25 default.")] int top = 25)
    {
        var before = await RequireScanAsync(beforeScanId);
        var after = await RequireScanAsync(afterScanId);
        var b = ToItems(await workspace.GetSnapshotsAsync(before.Id), out _);
        var a = ToItems(await workspace.GetSnapshotsAsync(after.Id), out _);
        return AdsToolEngine.ScanDiff(b, a, Math.Max(top, 0));
    }

    [McpServerTool(Name = "ads_changes_since", ReadOnly = true)]
    [Description("What moved since a scan: the newest control-plane change per scanned resource (ARG change feed), newest first, bounded to 200 rows with a truncated flag. Read-only.")]
    public async Task<ChangesSinceView> AdsChangesSince([Description("The stored scan id (exact).")] string scanId)
    {
        var scan = await RequireScanAsync(scanId);
        var since = scan.FinishedAt ?? scan.StartedAt;
        var ids = (await workspace.GetSnapshotsAsync(scan.Id))
            .Select(r => r.ResourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Same "moved since scan" shape as the desktop ChangeFeedService: newest change per resource.
        var latest = new Dictionary<string, ChangeFeedRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in (await workspace.ListChangeFeedAsync())
                     .Where(c => c.ChangeTime > since && ids.Contains(c.TargetResourceId))
                     .OrderByDescending(c => c.ChangeTime))
            latest.TryAdd(c.TargetResourceId, c);

        var ordered = latest.Values.OrderByDescending(c => c.ChangeTime).ToList();
        return new ChangesSinceView(scanId, since,
            ordered.Take(200).Select(ToView).ToList(), ordered.Count > 200);
    }

    [McpServerTool(Name = "ads_cost_summary", ReadOnly = true)]
    [Description("Cost figures cached in the local workspace: the Azure Retail Prices meter cache (unit prices as stored, per-row provenance, buckets by meter-key dimension). NOT actual spend - no per-scan spend rows are persisted in the workspace, and scan_id does not filter the cache. Honest empty when the cache is empty. Read-only.")]
    public async Task<CostSummaryView> AdsCostSummary(
        [Description("Accepted for interface stability; the cost cache is global per meter, not per scan.")] string? scanId = null,
        [Description("Bucket dimension: region | itemId | skuName (meter-key segments); empty = no buckets.")] string? dimension = null)
    {
        var entries = await workspace.ListCostCacheAsync();
        var caveats = new List<string>
        {
            "the workspace cost cache holds cached Azure Retail Prices meter rows (unit prices), not actual spend",
        };
        if (scanId is not null)
            caveats.Add("scan_id does not filter the cost cache (it is global per meter)");

        if (entries.Count == 0)
            return new CostSummaryView(false, 0, 0m, null, null, [], [], caveats);

        var priced = entries.Select(e => (Entry: e, Price: MeterPrice(e), Currency: MeterCurrency(e)))
            .ToList();
        var total = priced.Sum(p => p.Price);
        var currencies = priced.Select(p => p.Currency).Where(c => c is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var asOf = entries.Max(e => e.FetchedAt);

        IReadOnlyList<CostBucketView> buckets = [];
        var dim = dimension?.Trim().ToLowerInvariant();
        var index = dim is "region" ? 1 : dim is "itemid" ? 0 : dim is "skuname" ? 2 : -1;
        if (index >= 0)
        {
            var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in priced)
            {
                var parts = p.Entry.Key.Split('|');
                if (parts.Length <= index || parts[index].Length == 0) continue;
                var key = parts[index];
                totals[key] = totals.TryGetValue(key, out var t) ? t + p.Price : p.Price;
            }
            var sum = totals.Values.Sum();
            buckets = totals
                .Select(t => new CostBucketView(t.Key, t.Value, sum == 0m ? 0d : (double)(t.Value / sum)))
                .OrderByDescending(b => b.Cost)
                .ThenBy(b => b.Key, StringComparer.Ordinal)
                .ToList();
        }

        return new CostSummaryView(true, entries.Count, total,
            currencies.Count == 1 ? currencies[0] : null, asOf, buckets,
            entries.Select(e => new CostMeterView(e.Key, e.Json, e.FetchedAt, e.ExpiresAt)).ToList(),
            caveats);
    }

    [McpServerTool(Name = "ads_designs_list", ReadOnly = true)]
    [Description("Every design saved in the local workspace: id, name, pending flag (not yet synced to the server), saved/synced timestamps. No content. Read-only.")]
    public async Task<IReadOnlyList<DesignView>> AdsDesignsList()
    {
        var designs = await workspace.ListDesignsAsync();
        return designs.Select(d => new DesignView(d.Id, d.Name, d.Pending, d.SavedAt, d.SyncedAt)).ToList();
    }

    [McpServerTool(Name = "ads_design_get", ReadOnly = true)]
    [Description("One saved design: the stored content JSON verbatim (the honest source) plus the canonical .ads.json text when the payload converts; a conversion failure leaves the text null with a caveat and never fails the call. Read-only.")]
    public async Task<DesignGetView> AdsDesignGet([Description("The stored design id (exact).")] string designId)
    {
        var design = await workspace.GetDesignAsync(designId);
        if (design is null)
            throw new McpException($"design not found: {designId}");

        string? text = null;
        string? caveat = null;
        try
        {
            // The canvas load path deserializes the saved payload the same way (default options).
            var graph = JsonSerializer.Deserialize<DiagramGraph>(design.Data);
            if (graph is null)
                caveat = "design payload is not a design graph; text conversion skipped";
            else
                text = AdsDesignText.Write(graph, design.Name);
        }
        catch (Exception ex)
        {
            caveat = $"text conversion failed: {ex.Message}";
            logger.LogDebug(ex, "ads_design_get text conversion failed for {DesignId}", designId);
        }

        return new DesignGetView(design.Id, design.Name, design.Data, text, caveat, design.Pending, design.SavedAt);
    }

    [McpServerTool(Name = "ads_exposure", ReadOnly = true)]
    [Description("Internet-exposure findings over a scan's inventory (open NSG inbound rules, public IPs, public PaaS endpoints, unapproved private endpoints). Read-only.")]
    public async Task<ExposureReport> AdsExposure([Description("The stored scan id (exact).")] string scanId)
    {
        var scan = await RequireScanAsync(scanId);
        var items = ToItems(await workspace.GetSnapshotsAsync(scan.Id), out _);
        return AdsToolEngine.Exposure(items);
    }

    [McpServerTool(Name = "ads_reach", ReadOnly = true)]
    [Description("Reachability query over a scan (v2 [16]): which paths reach a resource - proven from the snapshot only (attached public IPs, public PaaS endpoints, public LB frontends into backends, internet-open NSGs, private endpoints, peer vnets, same-subnet neighbours). A proven path is positive evidence; no path is NOT proof of closure, and un-inspected layers (publicNetworkAccess flags, NSG deny precedence, UDRs/firewall/WAF, RBAC) ride the caveats. A target outside the scan answers with a diagnostic, never a verdict. Read-only.")]
    public async Task<ReachReport> AdsReach(
        [Description("The stored scan id (exact).")] string scanId,
        [Description("Target resource id (case-insensitive ARM id as scanned).")] string targetId)
    {
        var scan = await RequireScanAsync(scanId);
        var items = ToItems(await workspace.GetSnapshotsAsync(scan.Id), out var malformed);
        var answer = AdsToolEngine.Reach(items, targetId);
        return malformed == 0
            ? answer
            : answer with { Caveats = [.. answer.Caveats, .. MalformedCaveats(malformed)] };
    }

    [McpServerTool(Name = "ads_unmanaged", ReadOnly = true)]
    [Description("Unmanaged / protected audit over a scan's inventory. The do-not-touch guardrail list is not persisted in the workspace, so the built-in default policy (no custom protected scopes, tags or types) is used - the caveat says so. Read-only.")]
    public async Task<UnmanagedView> AdsUnmanaged([Description("The stored scan id (exact).")] string scanId)
    {
        var scan = await RequireScanAsync(scanId);
        var items = ToItems(await workspace.GetSnapshotsAsync(scan.Id), out var malformed);
        var caveats = new List<string>
        {
            "default guardrail policy - no custom do-not-touch list persisted in workspace",
        };
        caveats.AddRange(MalformedCaveats(malformed));
        return new UnmanagedView(AdsToolEngine.Unmanaged(items, GuardrailPolicy.Empty),
            "default (no persisted workspace policy)", caveats);
    }

    [McpServerTool(Name = "ads_decom_plan", ReadOnly = true)]
    [Description("PLAN-ONLY decommission planning over a scan: staged delete order, refusals, blockers, impacts and the runbook identity (plan hash / protection window / purge-not-before). This tool never executes anything and the purge never runs from here; a refused plan carries no runbook identity. Uses the default guardrail policy (no persisted workspace policy); ARM lock / RBAC / backup evidence is not supplied, so the plan reports it as not evaluated. Read-only.")]
    public async Task<DecomView> AdsDecomPlan(
        [Description("The stored scan id (exact).")] string scanId,
        [Description("Comma-separated candidate resource ids.")] string candidatesCsv)
    {
        var scan = await RequireScanAsync(scanId);
        var items = ToItems(await workspace.GetSnapshotsAsync(scan.Id), out var malformed);
        var candidates = candidatesCsv
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (candidates.Count == 0)
            throw new McpException("no candidates supplied");

        var now = DateTimeOffset.UtcNow;
        var purgeDelay = TimeSpan.FromDays(7);
        var caveats = new List<string>
        {
            "default guardrail policy - no custom do-not-touch list persisted in workspace",
        };
        caveats.AddRange(MalformedCaveats(malformed));
        return new DecomView(
            AdsToolEngine.DecomPlan(items, candidates, GuardrailPolicy.Empty, now, purgeDelay),
            "default (no persisted workspace policy)", now, purgeDelay, caveats);
    }

    [McpServerTool(Name = "ads_decom_windows", ReadOnly = true)]
    [Description("Persisted decommission window state (plan hash, approved execution hash, state, per-scope outcome rows, timestamps). Windows are keyed by subscription + window id, not scan id; with no arguments the most recently updated window is returned. Read-only.")]
    public async Task<DecomWindowsView> AdsDecomWindows(
        [Description("Subscription id; omit for the most recently updated window.")] string? subscriptionId = null,
        [Description("Window id; omit for the most recently updated window.")] string? windowId = null)
    {
        var row = !string.IsNullOrWhiteSpace(subscriptionId) && !string.IsNullOrWhiteSpace(windowId)
            ? await workspace.GetDecomWindowAsync(subscriptionId, windowId)
            : await workspace.LatestDecomWindowAsync();
        return new DecomWindowsView(row?.SubscriptionId, row?.WindowId,
            row is null ? [] : [ToView(row)]);
    }

    // --- helpers ---

    private async Task<ScanRecord> RequireScanAsync(string scanId)
    {
        var scan = await workspace.GetScanAsync(scanId);
        if (scan is null)
            throw new McpException($"scan not found: {scanId}");
        return scan;
    }

    /// <summary>
    /// Per-row snapshot → inventory conversion: a malformed row (bad tags/properties JSON)
    /// is counted and skipped, never fatal; resumed-scan duplicate rows are deduplicated
    /// (the workspace contract for readers).
    /// </summary>
    private static List<InventoryItem> ToItems(IReadOnlyList<ResourceSnapshotRecord> rows, out int malformed)
    {
        var items = new List<InventoryItem>(rows.Count);
        malformed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!seen.Add(row.ResourceId)) continue;
            if (!TryConvert(row, out var item))
            {
                malformed++;
                continue;
            }
            items.Add(item!);
        }
        return items;
    }

    private static bool TryConvert(ResourceSnapshotRecord row, out InventoryItem? item)
    {
        item = null;
        try
        {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(row.TagsJson))
            {
                using var doc = JsonDocument.Parse(row.TagsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in doc.RootElement.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String)
                            tags[p.Name] = p.Value.GetString() ?? "";
            }

            JsonElement? raw = null;
            if (!string.IsNullOrWhiteSpace(row.PropertiesJson))
                raw = JsonDocument.Parse(row.PropertiesJson).RootElement.Clone();

            item = new InventoryItem(
                row.ResourceId, row.Name, row.ResourceType,
                row.Location ?? "", row.ResourceGroup ?? "", row.SubscriptionId ?? "",
                tags, raw);
            return true;
        }
        catch
        {
            item = null;
            return false;
        }
    }

    private static IReadOnlyList<string> MalformedCaveats(int malformed)
    {
        if (malformed == 0) return [];
        return [malformed + " malformed snapshot row(s) skipped"];
    }

    /// <summary>Sum of the retail prices in one cached meter row (array or object envelope).</summary>
    private static decimal MeterPrice(CostCacheEntry entry)
    {
        try
        {
            using var doc = JsonDocument.Parse(entry.Json);
            var root = doc.RootElement;
            var price = 0m;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    price += ElementPrice(el);
            }
            else if (root.TryGetProperty("retailPrice", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                    price += ElementPrice(el);
            }
            return price;
        }
        catch
        {
            return 0m; // unparseable cached payload: counted in the row, priced as 0
        }
    }

    private static string? MeterCurrency(CostCacheEntry entry)
    {
        try
        {
            using var doc = JsonDocument.Parse(entry.Json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("currencyCode", out var cc)
                   && cc.ValueKind == JsonValueKind.String
                ? cc.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static decimal ElementPrice(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty("retailPrice", out var p)
        && p.ValueKind == JsonValueKind.Number
            ? p.GetDecimal()
            : 0m;

    private static ChangeFeedView ToView(ChangeFeedRecord c) => new(
        c.TargetResourceId, c.TargetResourceType, c.ChangeType, c.ChangeTime,
        c.ChangedBy, c.Operation, c.ChangesCount, c.ChangedPropertiesJson);

    private static DecomWindowView ToView(DecomWindowModel w) => new(
        w.WindowId, w.ScanId, w.SubscriptionId, w.PlanHash, w.ExecutionHash, w.State, w.UpdatedAtUtc);
}

// --- view records (JSON payloads the host hands to the client) ---

/// <summary>One scan run row (ads_scans_list).</summary>
public sealed record ScanListView(
    string Id, string Scope, string ScopeType, string Status,
    int ResourceCount, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? Error);

/// <summary>One scan with its manifest and counts-by-type (ads_scan_get).</summary>
public sealed record ScanDetailView(
    string Id, string Scope, string ScopeType, string Status,
    int ResourceCount, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? Error,
    string? ManifestJson, IReadOnlyList<TypeCount> CountsByType);

/// <summary>A filtered inventory page plus host-side row caveats (ads_scan_inventory).</summary>
public sealed record ScanInventoryView(
    InventoryPage Page, int RowsAfterDedup, int MalformedRowsSkipped, IReadOnlyList<string> Caveats);

/// <summary>"What moved since the scan", bounded (ads_changes_since).</summary>
public sealed record ChangesSinceView(
    string ScanId, DateTimeOffset Since, IReadOnlyList<ChangeFeedView> Changes, bool Truncated);

/// <summary>One control-plane change row (ads_changes_since).</summary>
public sealed record ChangeFeedView(
    string TargetResourceId, string? TargetResourceType, string ChangeType,
    DateTimeOffset ChangeTime, string? ChangedBy, string? Operation,
    int ChangesCount, string? ChangedPropertiesJson);

/// <summary>The workspace cost cache summary (ads_cost_summary).</summary>
public sealed record CostSummaryView(
    bool HasCache, int MeterCount, decimal TotalRetailPrice, string? Currency,
    DateTimeOffset? AsOf, IReadOnlyList<CostBucketView> Buckets,
    IReadOnlyList<CostMeterView> Meters, IReadOnlyList<string> Caveats);

/// <summary>One cost bucket: what it costs and its share (0..1) of the bucketed total.</summary>
public sealed record CostBucketView(string Key, decimal Cost, double Share);

/// <summary>One cached meter row, price JSON as stored (ads_cost_summary).</summary>
public sealed record CostMeterView(string Key, string PriceJson, DateTimeOffset FetchedAt, DateTimeOffset ExpiresAt);

/// <summary>One saved design, no content (ads_designs_list).</summary>
public sealed record DesignView(
    string Id, string Name, bool Pending, DateTimeOffset SavedAt, DateTimeOffset? SyncedAt);

/// <summary>One saved design with verbatim content and best-effort text (ads_design_get).</summary>
public sealed record DesignGetView(
    string Id, string Name, string ContentJson, string? AdsDesignText,
    string? TextCaveat, bool Pending, DateTimeOffset SavedAt);

/// <summary>The unmanaged audit plus the policy source and caveats (ads_unmanaged).</summary>
public sealed record UnmanagedView(UnmanagedReport Report, string PolicySource, IReadOnlyList<string> Caveats);

/// <summary>PLAN-ONLY decommission plan plus policy source, injected clock and caveats (ads_decom_plan).</summary>
public sealed record DecomView(
    DecomReport Report, string PolicySource, DateTimeOffset Now, TimeSpan PurgeDelay,
    IReadOnlyList<string> Caveats);

/// <summary>Persisted decommission window rows (ads_decom_windows).</summary>
public sealed record DecomWindowsView(
    string? SubscriptionId, string? WindowId, IReadOnlyList<DecomWindowView> Windows);

/// <summary>One decommission window's stored state (ads_decom_windows).</summary>
public sealed record DecomWindowView(
    string WindowId, string ScanId, string SubscriptionId,
    string PlanHash, string ExecutionHash, string State, DateTimeOffset UpdatedAtUtc);

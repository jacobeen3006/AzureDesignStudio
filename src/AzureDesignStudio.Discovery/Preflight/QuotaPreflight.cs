using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using static AzureDesignStudio.Discovery.Preflight.PreflightSupport;

namespace AzureDesignStudio.Discovery.Preflight;

/// <summary>Instances of one VM size the previewed batch adds in one location (lower case, no
/// spaces). Negative: released by a Modify (a resize, a smaller scale set or node pool).</summary>
public sealed record ComputeDemand(string Location, string VmSize, int Instances);

/// <summary>Public IP addresses of one SKU (Standard or Basic) the batch creates in one location.</summary>
public sealed record IpDemand(string Location, string Sku, int Count);

/// <summary>What the batch asks of the subscription's quotas, summed per (location, size) and
/// (location, SKU), with a note for every resource that could not be counted.</summary>
public sealed record QuotaDemandSet(IReadOnlyList<ComputeDemand> Compute, IReadOnlyList<IpDemand> PublicIps, IReadOnlyList<string> Notes);

public enum QuotaStatus { Ok, NearLimit, Exceeds, Unknown, SkuUnavailable }

/// <summary>
/// One quota in one location: a VM family's vCPUs (<see cref="VmSize"/> names the requested
/// sizes), the regional total <c>cores</c>, a public IP quota, or a VM size that cannot be
/// deployed there (<see cref="QuotaStatus.SkuUnavailable"/>, <see cref="QuotaName"/> = the size).
/// <see cref="Requested"/> is in the quota's unit (vCPUs, addresses) and counts only the increase.
/// <see cref="QuotaStatus.Unknown"/> carries its reason in <see cref="Detail"/>; Current and Limit
/// are 0 when no usage entry was read.
/// </summary>
public sealed record QuotaFinding(string Location, string QuotaName, string DisplayName, long Current, long Limit, long Requested,
    QuotaStatus Status, string? VmSize, string? Detail);

public sealed record QuotaPreflightResult(IReadOnlyList<QuotaFinding> Findings, IReadOnlyList<string> Notes);

/// <summary>
/// Wave B4: turns the after payloads of a successful what-if into vCPU and public IP demand
/// (<see cref="DemandFrom"/>; a Modify counts only the increase) and checks it against Resource
/// SKUs and the compute and network usages of every location with demand. Advisory only: ARM
/// still enforces quota at deployment. Never a false Ok: a located resource whose size or
/// instance count is unknown, a SKU without a vCPU count and every read that fails make the
/// affected checks Unknown; spot (a separate pool) is a note. Never throws
/// except an <see cref="OperationCanceledException"/> from the caller's token: no permission,
/// throttling, a malformed answer and the time limit all become notes, with what was read so far.
/// </summary>
public sealed class QuotaPreflight(ArmTransport transport)
{
    public const string ComputeUsagesApiVersion = "2026-04-01";
    public const string SkusApiVersion = "2021-07-01";
    public const string NetworkUsagesApiVersion = "2026-01-01";
    public const double NearLimitRatio = 0.9;

    public static IReadOnlyList<string> Caveats { get; } =
    [
        "Quota is not capacity: Azure can still be out of capacity for a size in a region.",
        "Limits are per subscription and per region.",
        "AKS upgrades temporarily use extra vCPUs.",
        "The family-to-quota mapping follows the Resource SKUs family name.",
        "Only the previewed batch is counted.",
    ];

    private const string ForbiddenNote =
        "No permission to read quotas (needs Microsoft.Compute/locations/usages/read, Microsoft.Compute/skus/read and Microsoft.Network/locations/usages/read).";

    private const string SpotNote = "Spot quota is a separate pool and is not checked.";
    private const string NotCheckedTypesNote = "Quota for App Service plans, Container Apps and Container Instances is not checked.";
    private const string NoSubscriptionNote = "Quota was not checked: the deployment names no subscription.";
    private const string NoVcpuCount = "SKU reports no vCPU count";
    private const string NoFamily = "SKU reports no family";

    private const string RegionalCores = "cores";
    private const string PublicIpQuota = "PublicIPAddresses";
    private const string StandardIpQuota = "StandardSkuPublicIpAddresses";

    private const string VirtualMachines = "Microsoft.Compute/virtualMachines";
    private const string ScaleSets = "Microsoft.Compute/virtualMachineScaleSets";
    private const string ManagedClusters = "Microsoft.ContainerService/managedClusters";
    private const string AgentPools = "Microsoft.ContainerService/managedClusters/agentPools";
    private const string PublicIpAddresses = "Microsoft.Network/publicIPAddresses";

    private static readonly HashSet<string> QuotaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        VirtualMachines, ScaleSets, ManagedClusters, AgentPools, PublicIpAddresses,
    };

    /// <summary>Compute the scope decisions leave out: noted, never counted.</summary>
    private static readonly HashSet<string> NotCheckedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Web/serverfarms",
        "Microsoft.App/containerApps",
        "Microsoft.App/jobs",
        "Microsoft.App/managedEnvironments",
        "Microsoft.ContainerInstance/containerGroups",
    };

    // ---------- demand (pure) ----------

    /// <summary>
    /// The quota demand of the Create and Modify rows, read from <c>AfterJson</c> minus
    /// <c>BeforeJson</c> on a Modify, in resource id order (ordinal; a size keeps the casing of
    /// its first resource). VMs count their <c>hardwareProfile.vmSize</c>; scale sets
    /// <c>sku.name</c> x <c>sku.capacity</c>; AKS node pools (cluster profiles and
    /// <c>agentPools</c> children, which take their cluster's location) <c>vmSize</c> x
    /// <c>maxCount</c> when autoscaling, else <c>count</c>; public IPs one of <c>sku.name</c>
    /// (default Standard) per Create. Spot and low-priority capacity is a separate pool and is
    /// skipped with one note; a resource without a location or size is skipped with a note naming
    /// it. Sums of zero are dropped; ordered by location, then size or SKU (ordinal).
    /// </summary>
    public static QuotaDemandSet DemandFrom(IReadOnlyList<WhatIfChange> changes) => DemandFrom(changes, out _);

    /// <summary><see cref="DemandFrom(IReadOnlyList{WhatIfChange})"/>, plus every compute resource
    /// that has a location but was not counted (its size or instance count is unknown): it still
    /// draws on that location's quotas, so <see cref="CheckAsync"/> cannot judge them Ok.</summary>
    private static QuotaDemandSet DemandFrom(IReadOnlyList<WhatIfChange> changes, out IReadOnlyList<UncountedCompute> uncounted)
    {
        var all = (changes ?? []).Where(c => c is not null).ToList();
        var demand = new DemandBuilder(all);
        foreach (var change in all
            .Where(c => c.Kind is WhatIfChangeKind.Create or WhatIfChangeKind.Modify)
            .OrderBy(c => c.ResourceId ?? "", StringComparer.Ordinal))
            demand.Add(change);
        uncounted = demand.Uncounted;
        return demand.Build();
    }

    /// <summary>A located compute resource left out of the demand; <see cref="VmSize"/> is null
    /// when the size itself is unknown.</summary>
    private sealed record UncountedCompute(string Location, string? VmSize, string ResourceId);

    private sealed class DemandBuilder(IReadOnlyList<WhatIfChange> all)
    {
        private readonly List<ComputeDemand> _compute = [];
        private readonly List<IpDemand> _ips = [];
        private readonly List<string> _notes = [];
        private readonly List<UncountedCompute> _uncounted = [];
        private bool _spot;
        private bool _notCheckedType;

        public IReadOnlyList<UncountedCompute> Uncounted => _uncounted;

        public void Add(WhatIfChange change)
        {
            var type = TypeOf(change.ResourceId);
            try
            {
                var after = ParseObject(change.AfterJson, out var unreadable);
                type = Text(after, "type") ?? type;
                if (type is null)
                    return;
                if (NotCheckedTypes.Contains(type))
                {
                    _notCheckedType = true;
                    return;
                }
                if (!QuotaTypes.Contains(type))
                    return;

                var id = change.ResourceId ?? Text(after, "id") ?? type;
                if (after is null)
                {
                    NotCounted(id, unreadable!);
                    return;
                }
                var before = change.Kind == WhatIfChangeKind.Modify ? ParseObject(change.BeforeJson, out _) : null;
                if (type.Equals(VirtualMachines, StringComparison.OrdinalIgnoreCase))
                    AddVirtualMachine(id, after, before);
                else if (type.Equals(ScaleSets, StringComparison.OrdinalIgnoreCase))
                    AddScaleSet(id, after, before);
                else if (type.Equals(ManagedClusters, StringComparison.OrdinalIgnoreCase))
                    AddCluster(id, after, before);
                else if (type.Equals(AgentPools, StringComparison.OrdinalIgnoreCase))
                    AddAgentPool(id, after, before);
                else if (change.Kind == WhatIfChangeKind.Create)
                    AddPublicIp(id, after);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A payload System.Text.Json cannot materialize (duplicate members, ...).
                if (type is not null && QuotaTypes.Contains(type))
                    NotCounted(change.ResourceId ?? type, "the what-if result for it could not be read");
            }
        }

        /// <summary>A resize adds one of the new size and releases one of the old, so a
        /// same-family resize asks only for the vCPU difference.</summary>
        private void AddVirtualMachine(string id, JsonObject after, JsonObject? before)
        {
            if (IsSpot(after))
            {
                _spot = true;
                return;
            }
            if (!Located(id, after, out var location))
                return;
            if (Text(after, "properties", "hardwareProfile", "vmSize") is not { } size)
            {
                NotCounted(id, "its VM size is unknown", location, null);
                return;
            }
            var oldSize = before is null || IsSpot(before) ? null : Text(before, "properties", "hardwareProfile", "vmSize");
            if (oldSize is not null && SameName(oldSize, size))
                return;
            AddCompute(location, size, 1);
            if (oldSize is not null)
                AddCompute(location, oldSize, -1);
        }

        private void AddScaleSet(string id, JsonObject after, JsonObject? before)
        {
            if (IsSpot(after))
            {
                _spot = true;
                return;
            }
            var size = Text(after, "sku", "name");
            // A Flexible scale set without a VM profile has no instances of its own: its VMs
            // are virtualMachines rows.
            if (size is null && Node(after, "properties", "virtualMachineProfile") is null)
                return;
            if (!Located(id, after, out var location))
                return;
            if (size is null)
            {
                NotCounted(id, "its VM size is unknown", location, null);
                return;
            }
            if (Count(after, "sku", "capacity") is not { } capacity)
            {
                NotCounted(id, "its instance count is unknown", location, size);
                return;
            }
            AddCompute(location, size, capacity);
            if (before is not null && !IsSpot(before)
                && Text(before, "sku", "name") is { } oldSize && Count(before, "sku", "capacity") is { } oldCapacity)
                AddCompute(location, oldSize, -oldCapacity);
        }

        /// <summary>Each <c>agentPoolProfiles[]</c> entry; a Modify subtracts the before pool of
        /// the same name (case-insensitive). A pool that the after payload no longer lists is not
        /// released.</summary>
        private void AddCluster(string id, JsonObject after, JsonObject? before)
        {
            if (Node(after, "properties", "agentPoolProfiles") is not JsonArray { Count: > 0 } pools)
                return;
            if (!Located(id, after, out var location))
                return;
            var beforePools = Node(before, "properties", "agentPoolProfiles") as JsonArray;
            foreach (var pool in pools.OfType<JsonObject>())
            {
                var name = Text(pool, "name");
                var previous = name is null
                    ? null
                    : beforePools?.OfType<JsonObject>().FirstOrDefault(p => Text(p, "name") is { } n && SameName(n, name));
                AddPool(id, $"agent pool '{name ?? "(unnamed)"}'", location, pool, previous);
            }
        }

        /// <summary>A <c>managedClusters/agentPools</c> child has no location of its own: it
        /// takes its cluster's, from the cluster's row in the same what-if.</summary>
        private void AddAgentPool(string id, JsonObject after, JsonObject? before)
        {
            var properties = Node(after, "properties") as JsonObject ?? [];
            if (IsSpotPriority(Text(properties, "scaleSetPriority")))
            {
                _spot = true;
                return;
            }
            if ((LocationOf(after) ?? ClusterLocation(id)) is not { } location)
            {
                NotCounted(id, "the location of its cluster is unknown");
                return;
            }
            AddPool(id, "the agent pool", location, properties, Node(before, "properties") as JsonObject);
        }

        private void AddPool(string id, string pool, string location, JsonObject profile, JsonObject? previous)
        {
            if (IsSpotPriority(Text(profile, "scaleSetPriority")))
            {
                _spot = true;
                return;
            }
            if (Text(profile, "vmSize") is not { } size)
            {
                NotCounted(id, $"{pool} has no VM size", location, null);
                return;
            }
            if (NodeCount(profile) is not { } count)
            {
                NotCounted(id, $"{pool} has no node count", location, size);
                return;
            }
            AddCompute(location, size, count);
            if (previous is not null && !IsSpotPriority(Text(previous, "scaleSetPriority"))
                && Text(previous, "vmSize") is { } oldSize && NodeCount(previous) is { } oldCount)
                AddCompute(location, oldSize, -oldCount);
        }

        private void AddPublicIp(string id, JsonObject after)
        {
            if (!Located(id, after, out var location))
                return;
            var sku = Text(after, "sku", "name") ?? "Standard";
            sku = sku.Equals("Standard", StringComparison.OrdinalIgnoreCase) ? "Standard"
                : sku.Equals("Basic", StringComparison.OrdinalIgnoreCase) ? "Basic"
                : sku;
            _ips.Add(new IpDemand(location, sku, 1));
        }

        private string? ClusterLocation(string agentPoolId)
        {
            if (ParentOf(agentPoolId) is not { } cluster)
                return null;
            foreach (var change in all)
            {
                if (change.ResourceId is not { } candidate || !candidate.TrimEnd('/').Equals(cluster, StringComparison.OrdinalIgnoreCase))
                    continue;
                if ((LocationOf(ParseObject(change.AfterJson, out _)) ?? LocationOf(ParseObject(change.BeforeJson, out _))) is { } location)
                    return location;
            }
            return null;
        }

        private bool Located(string id, JsonObject resource, out string location)
        {
            location = LocationOf(resource) ?? "";
            if (location.Length > 0)
                return true;
            NotCounted(id, "it has no location");
            return false;
        }

        private void AddCompute(string location, string size, int instances) => _compute.Add(new ComputeDemand(location, size, instances));

        private void NotCounted(string id, string reason) => _notes.Add($"{id} is not counted for quota: {reason}.");

        /// <summary>The note, plus a marker: the resource still draws on <paramref name="location"/>'s quotas.</summary>
        private void NotCounted(string id, string reason, string location, string? size)
        {
            NotCounted(id, reason);
            _uncounted.Add(new UncountedCompute(location, size, id));
        }

        public QuotaDemandSet Build()
        {
            var compute = _compute
                .GroupBy(d => (d.Location, Size: d.VmSize.ToUpperInvariant()))
                .Select(g => new ComputeDemand(g.Key.Location, g.First().VmSize, Clamp(g.Sum(d => (long)d.Instances))))
                .Where(d => d.Instances != 0)
                .OrderBy(d => d.Location, StringComparer.Ordinal)
                .ThenBy(d => d.VmSize, StringComparer.Ordinal)
                .ToList();
            var ips = _ips
                .GroupBy(d => (d.Location, Sku: d.Sku.ToUpperInvariant()))
                .Select(g => new IpDemand(g.Key.Location, g.First().Sku, Clamp(g.Sum(d => (long)d.Count))))
                .Where(d => d.Count != 0)
                .OrderBy(d => d.Location, StringComparer.Ordinal)
                .ThenBy(d => d.Sku, StringComparer.Ordinal)
                .ToList();

            var notes = new List<string>(_notes);
            if (_spot)
                notes.Add(SpotNote);
            if (_notCheckedType)
                notes.Add(NotCheckedTypesNote);
            return new QuotaDemandSet(compute, ips, notes);
        }

        private static int Clamp(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    }

    private static bool IsSpot(JsonObject resource) =>
        IsSpotPriority(Text(resource, "properties", "priority"))
        || IsSpotPriority(Text(resource, "properties", "virtualMachineProfile", "priority"));

    /// <summary>Spot, or the older low-priority VMs: both draw on the separate low-priority quota.</summary>
    private static bool IsSpotPriority(string? priority) =>
        priority is not null
        && (priority.Equals("Spot", StringComparison.OrdinalIgnoreCase) || priority.Equals("Low", StringComparison.OrdinalIgnoreCase));

    private static int? NodeCount(JsonObject pool) =>
        Flag(pool, "enableAutoScaling") == true ? Count(pool, "maxCount") : Count(pool, "count");

    private static bool SameName(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    // ---------- check ----------

    /// <summary>
    /// Reads, per location with demand, the Resource SKUs and compute usages (VM sizes) and the
    /// network usages (public IPs), all at once, within <paramref name="budget"/>; changes in
    /// another subscription than <paramref name="subscriptionId"/> are not counted (note). A
    /// location with only releases, and no compute resource there left uncounted, needs no reads;
    /// no demand at all makes no calls.
    /// </summary>
    public async Task<QuotaPreflightResult> CheckAsync(string subscriptionId, IReadOnlyList<WhatIfChange> changes, TimeSpan budget,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return new QuotaPreflightResult([], [NoSubscriptionNote]);
        var subscription = subscriptionId.Trim();

        var ours = new List<WhatIfChange>();
        var elsewhere = false;
        foreach (var change in (changes ?? []).Where(c => c is not null))
        {
            if (SubscriptionOf(change.ResourceId) is { } owner && !owner.Equals(subscription, StringComparison.OrdinalIgnoreCase))
                elsewhere |= CountsForQuota(change);
            else
                ours.Add(change);
        }

        var demand = DemandFrom(ours, out var uncounted);
        var trailing = new List<string>(demand.Notes);
        if (elsewhere)
            trailing.Add($"Quota is checked only in subscription {subscription}; resources this deployment changes in other subscriptions are not counted.");

        // A location whose only compute is uncounted is still read: its checks must say Unknown.
        var computeLocations = demand.Compute.Where(d => d.Instances > 0).Select(d => d.Location)
            .Concat(uncounted.Select(u => u.Location))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var ipLocations = demand.PublicIps.Where(d => d.Count > 0).Select(d => d.Location)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var reads = new List<QuotaRead>();
        foreach (var location in computeLocations)
        {
            reads.Add(new QuotaRead(location, Source.Skus, SkusUrl(subscription, location)));
            reads.Add(new QuotaRead(location, Source.ComputeUsages, UsagesUrl(subscription, "Microsoft.Compute", location, ComputeUsagesApiVersion)));
        }
        foreach (var location in ipLocations)
            reads.Add(new QuotaRead(location, Source.NetworkUsages, UsagesUrl(subscription, "Microsoft.Network", location, NetworkUsagesApiVersion)));
        if (reads.Count == 0)
            return new QuotaPreflightResult([], trailing);

        var limit = DeadlineOf(budget);
        using var deadline = new CancellationTokenSource(limit);
        if (limit == TimeSpan.Zero)
            deadline.Cancel();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var tasks = reads.Select(r => ReadAsync(r.Url, linked.Token, deadline.Token, ct)).ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Rethrown below as the caller's own cancellation.
        }
        ct.ThrowIfCancellationRequested();

        var outcomes = new Dictionary<(string, Source), ReadOutcome>();
        for (var i = 0; i < reads.Count; i++)
            outcomes[(reads[i].Location, reads[i].Source)] = tasks[i].Result;

        var findings = new List<QuotaFinding>();
        var zoneRestricted = new List<string>();
        foreach (var location in computeLocations)
            JudgeCompute(location, demand.Compute.Where(d => d.Location == location).ToList(), uncounted.Where(u => u.Location == location).ToList(),
                outcomes[(location, Source.Skus)], outcomes[(location, Source.ComputeUsages)], findings, zoneRestricted);
        foreach (var location in ipLocations)
            JudgePublicIps(location, demand.PublicIps.Where(d => d.Location == location).ToList(),
                outcomes[(location, Source.NetworkUsages)], findings);

        var notes = new List<string>();
        if (outcomes.Values.Any(o => o.Kind == ReadKind.Forbidden))
            notes.Add(ForbiddenNote);
        var timedOut = outcomes.Values.Count(o => o.Kind == ReadKind.TimedOut);
        if (timedOut > 0)
            notes.Add($"Quota check stopped at the time limit: {timedOut} of {reads.Count} quota reads did not finish.");
        foreach (var read in reads)
        {
            if (outcomes[(read.Location, read.Source)] is { Kind: ReadKind.Failed } failed)
                notes.Add($"Quota in {read.Location} was not fully checked: {What(read.Source)} could not be read ({failed.Code}).");
        }
        if (zoneRestricted.Count > 0)
            notes.Add($"Zone restrictions apply to {string.Join(", ", zoneRestricted)}; zones are not checked.");
        notes.AddRange(trailing);

        return new QuotaPreflightResult(Order(findings), notes);
    }

    private enum Source { Skus, ComputeUsages, NetworkUsages }

    private enum ReadKind { Read, Forbidden, TimedOut, Failed }

    private sealed record QuotaRead(string Location, Source Source, string Url);

    private sealed record ReadOutcome(ReadKind Kind, IReadOnlyList<JsonElement> Items, string? Code)
    {
        public static ReadOutcome Forbidden { get; } = new(ReadKind.Forbidden, [], null);
        public static ReadOutcome TimedOut { get; } = new(ReadKind.TimedOut, [], null);
    }

    private async Task<ReadOutcome> ReadAsync(string url, CancellationToken requestToken, CancellationToken deadlineToken, CancellationToken ct)
    {
        // After the time limit no new read starts.
        if (deadlineToken.IsCancellationRequested)
            return ReadOutcome.TimedOut;
        try
        {
            var items = await transport.GetListAsync(url, "value", requestToken).ConfigureAwait(false);
            return new ReadOutcome(ReadKind.Read, items, null);
        }
        catch (Exception e)
        {
            return Classify(e, deadlineToken, ct) switch
            {
                RequestFailure.TimedOut => ReadOutcome.TimedOut,
                RequestFailure.Forbidden => ReadOutcome.Forbidden,
                _ => new ReadOutcome(ReadKind.Failed, [], CodeOf(e)),
            };
        }
    }

    private string SkusUrl(string subscription, string location) =>
        $"{transport.Endpoints.ManagementEndpoint}/subscriptions/{Uri.EscapeDataString(subscription)}/providers/Microsoft.Compute/skus"
        + $"?api-version={SkusApiVersion}&$filter={Uri.EscapeDataString($"location eq '{location.Replace("'", "''", StringComparison.Ordinal)}'")}";

    private string UsagesUrl(string subscription, string provider, string location, string apiVersion) =>
        $"{transport.Endpoints.ManagementEndpoint}/subscriptions/{Uri.EscapeDataString(subscription)}/providers/{provider}"
        + $"/locations/{Uri.EscapeDataString(location)}/usages?api-version={apiVersion}";

    private static string What(Source source) => source switch
    {
        Source.Skus => "the Resource SKUs",
        Source.ComputeUsages => "the compute usages",
        _ => "the network usages",
    };

    /// <summary>Why every check that needed this read is Unknown; null when it was read.</summary>
    private static string? FailureOf(ReadOutcome outcome, Source source) => outcome.Kind switch
    {
        ReadKind.Read => null,
        ReadKind.Forbidden => $"no permission to read {What(source)}",
        ReadKind.TimedOut => $"{What(source)} were not read within the time limit",
        _ => $"{What(source)} could not be read ({outcome.Code})",
    };

    private static string? JoinDetails(params string?[] details) =>
        details.Where(d => d is not null).ToList() is { Count: > 0 } present ? string.Join("; ", present) : null;

    private static bool CountsForQuota(WhatIfChange change)
    {
        if (change.Kind is not (WhatIfChangeKind.Create or WhatIfChangeKind.Modify))
            return false;
        string? type;
        try
        {
            type = Text(ParseObject(change.AfterJson, out _), "type") ?? TypeOf(change.ResourceId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            type = TypeOf(change.ResourceId);
        }
        return type is not null && QuotaTypes.Contains(type);
    }

    // ---------- judgement ----------

    /// <summary>
    /// One location's VM sizes: SkuUnavailable for a size not offered or restricted there; one
    /// check per VM family (sizes mapped by the SKU's <c>family</c>) and the regional
    /// <c>cores</c> check, each requesting max(0, the sum of instances x vCPUs). A size without a
    /// vCPU count, or not offered, makes its checks Unknown; so does a resource that was not
    /// counted (<paramref name="uncounted"/>): <c>cores</c>, and its size's family when the size is
    /// known. A family with only releases is not checked. Without the SKU list each size is
    /// Unknown under its own name.
    /// </summary>
    private static void JudgeCompute(string location, IReadOnlyList<ComputeDemand> entries, IReadOnlyList<UncountedCompute> uncounted,
        ReadOutcome skuRead, ReadOutcome usageRead, List<QuotaFinding> findings, List<string> zoneRestricted)
    {
        var usages = usageRead.Kind == ReadKind.Read ? UsageRows(usageRead.Items) : null;
        var failure = JoinDetails(FailureOf(usageRead, Source.ComputeUsages), FailureOf(skuRead, Source.Skus));

        if (skuRead.Kind != ReadKind.Read)
        {
            foreach (var entry in entries.Where(e => e.Instances > 0))
                findings.Add(new QuotaFinding(location, entry.VmSize, entry.VmSize, 0, 0, 0, QuotaStatus.Unknown, entry.VmSize, failure));
            findings.Add(Judge(location, RegionalCores, 0, null, usages, failure, []));
            return;
        }

        var skus = SkuIndex(skuRead.Items, location);
        var resolved = entries.Select(e => new Resolved(e, skus.GetValueOrDefault(e.VmSize))).ToList();
        var coreReasons = new List<string>();
        foreach (var (demand, sku) in resolved.Where(r => r.Demand.Instances > 0))
        {
            if (sku is null)
            {
                findings.Add(new QuotaFinding(location, demand.VmSize, demand.VmSize, 0, 0, 0, QuotaStatus.SkuUnavailable, demand.VmSize,
                    $"not offered in {location}"));
                coreReasons.Add($"{demand.VmSize} is not offered in {location}, so its vCPUs are unknown");
                continue;
            }
            if (sku.LocationRestricted)
                findings.Add(new QuotaFinding(location, demand.VmSize, demand.VmSize, 0, 0, Requested([new Resolved(demand, sku)]),
                    QuotaStatus.SkuUnavailable, demand.VmSize, $"not available to this subscription in {location}"));
            else if (sku.ZoneRestricted)
                zoneRestricted.Add($"{demand.VmSize} in {location}");
            if (sku.VCpus is null)
                coreReasons.Add(NoVcpuCount);
        }

        // An uncounted resource draws on the regional total, and on its size's family when the
        // size is known and offered: what was counted there is not all of the request.
        var missing = uncounted
            .Select(u => new Missing($"{u.ResourceId} is not counted", u.VmSize, u.VmSize is null ? null : skus.GetValueOrDefault(u.VmSize)?.Family))
            .ToList();
        coreReasons.AddRange(missing.Select(m => m.Reason));

        var families = resolved.Select(r => r.Sku?.Family).Concat(missing.Select(m => m.Family)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var family in families)
        {
            var members = resolved.Where(r => r.Sku?.Family is { } f && SameName(f, family)).ToList();
            var left = missing.Where(m => m.Family is { } f && SameName(f, family)).ToList();
            var sizes = members.Where(m => m.Demand.Instances > 0).Select(m => m.Demand.VmSize)
                .Concat(left.Select(m => m.VmSize!))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (sizes.Count == 0)
                continue;
            var reasons = new List<string>();
            if (members.Any(m => m.Demand.Instances > 0 && m.Sku!.VCpus is null))
                reasons.Add(NoVcpuCount);
            reasons.AddRange(left.Select(m => m.Reason).Distinct());
            findings.Add(Judge(location, family, Requested(members), string.Join(", ", sizes), usages, failure, reasons));
        }
        foreach (var entry in resolved.Where(r => r.Sku is { Family: null } && r.Demand.Instances > 0))
            findings.Add(new QuotaFinding(location, entry.Demand.VmSize, entry.Demand.VmSize, 0, 0, Requested([entry]), QuotaStatus.Unknown,
                entry.Demand.VmSize, failure ?? NoFamily));

        findings.Add(Judge(location, RegionalCores, Requested(resolved), null, usages, failure, coreReasons.Distinct().ToList()));
    }

    /// <summary>Standard IPs count against <c>StandardSkuPublicIpAddresses</c> when the location
    /// reports it, else against <c>PublicIPAddresses</c>; Basic IPs against <c>PublicIPAddresses</c>.</summary>
    private static void JudgePublicIps(string location, IReadOnlyList<IpDemand> entries, ReadOutcome read, List<QuotaFinding> findings)
    {
        var usages = read.Kind == ReadKind.Read ? UsageRows(read.Items) : null;
        var failure = FailureOf(read, Source.NetworkUsages);
        var standardQuota = usages is not null && !usages.ContainsKey(StandardIpQuota) ? PublicIpQuota : StandardIpQuota;
        foreach (var quota in entries
            .Where(e => e.Count > 0)
            .GroupBy(e => e.Sku.Equals("Basic", StringComparison.OrdinalIgnoreCase) ? PublicIpQuota : standardQuota))
            findings.Add(Judge(location, quota.Key, quota.Sum(e => (long)e.Count), null, usages, failure, []));
    }

    private sealed record Resolved(ComputeDemand Demand, SkuInfo? Sku);

    /// <summary>An uncounted resource as a check sees it: why the check is Unknown, its size and
    /// the size's family (null when either is unknown).</summary>
    private sealed record Missing(string Reason, string? VmSize, string? Family);

    /// <summary>Instances x vCPUs over the sizes whose vCPU count is known, never below zero and
    /// saturating at <see cref="long.MaxValue"/>. A release whose vCPUs are unknown is not
    /// subtracted, so the request can only be too high.</summary>
    private static long Requested(IEnumerable<Resolved> members)
    {
        decimal total = 0;
        foreach (var member in members)
        {
            if (member.Sku?.VCpus is { } vCpus)
                total += (decimal)member.Demand.Instances * vCpus;
        }
        return total <= 0 ? 0 : total >= long.MaxValue ? long.MaxValue : (long)total;
    }

    private static QuotaFinding Judge(string location, string quotaName, long requested, string? vmSize,
        IReadOnlyDictionary<string, UsageRow>? usages, string? failure, IReadOnlyCollection<string> reasons)
    {
        var row = usages?.GetValueOrDefault(quotaName);
        var name = row?.Name ?? quotaName;
        var display = row?.DisplayName ?? name;
        long current = row?.Current ?? 0, limit = row?.Limit ?? 0;

        if (failure is not null)
            return new QuotaFinding(location, name, display, current, limit, requested, QuotaStatus.Unknown, vmSize, failure);
        if (row is null)
            return new QuotaFinding(location, name, display, 0, 0, requested, QuotaStatus.Unknown, vmSize, $"no usage entry named '{quotaName}'");
        if (row.Current is null || row.Limit is null)
            return new QuotaFinding(location, name, display, current, limit, requested, QuotaStatus.Unknown, vmSize,
                $"the usage entry named '{name}' reports no current value or limit");
        if (reasons.Count > 0)
            return new QuotaFinding(location, name, display, current, limit, requested, QuotaStatus.Unknown, vmSize, string.Join("; ", reasons));

        var total = (decimal)current + requested;
        var status = total > limit ? QuotaStatus.Exceeds
            : total >= (decimal)NearLimitRatio * limit ? QuotaStatus.NearLimit
            : QuotaStatus.Ok;
        return new QuotaFinding(location, name, display, current, limit, requested, status, vmSize, null);
    }

    private static IReadOnlyList<QuotaFinding> Order(IEnumerable<QuotaFinding> findings) =>
        findings
            .OrderBy(f => f.Status switch
            {
                QuotaStatus.SkuUnavailable => 0,
                QuotaStatus.Exceeds => 1,
                QuotaStatus.NearLimit => 2,
                QuotaStatus.Unknown => 3,
                _ => 4,
            })
            .ThenBy(f => f.Location, StringComparer.Ordinal)
            .ThenBy(f => f.QuotaName, StringComparer.Ordinal)
            .ThenBy(f => f.VmSize ?? "", StringComparer.Ordinal)
            .ToList();

    // ---------- responses ----------

    private sealed record SkuInfo(string? Family, int? VCpus, bool LocationRestricted, bool ZoneRestricted);

    private sealed record UsageRow(string Name, string DisplayName, long? Current, long? Limit);

    /// <summary>The <c>virtualMachines</c> entries offered in <paramref name="location"/>, by
    /// name (case-insensitive; the first entry wins).</summary>
    private static Dictionary<string, SkuInfo> SkuIndex(IEnumerable<JsonElement> items, string location)
    {
        var index = new Dictionary<string, SkuInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var sku in items)
        {
            if (!string.Equals(Str(sku, "resourceType"), "virtualMachines", StringComparison.OrdinalIgnoreCase) || Str(sku, "name") is not { } name)
                continue;
            // The listing is filtered by location; an entry that names other locations only is not offered here.
            if (sku.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array
                && locations.GetArrayLength() > 0 && !Mentions(locations, location))
                continue;

            int? vCpus = null;
            if (sku.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array)
            {
                foreach (var capability in capabilities.EnumerateArray())
                {
                    if (string.Equals(Str(capability, "name"), "vCPUs", StringComparison.OrdinalIgnoreCase))
                    {
                        vCpus = PositiveInt(capability, "value");
                        break;
                    }
                }
            }

            bool locationRestricted = false, zoneRestricted = false;
            if (sku.TryGetProperty("restrictions", out var restrictions) && restrictions.ValueKind == JsonValueKind.Array)
            {
                foreach (var restriction in restrictions.EnumerateArray())
                {
                    var appliesHere =
                        (restriction.ValueKind == JsonValueKind.Object && restriction.TryGetProperty("values", out var values) && Mentions(values, location))
                        || (restriction.ValueKind == JsonValueKind.Object && restriction.TryGetProperty("restrictionInfo", out var info)
                            && info.ValueKind == JsonValueKind.Object && info.TryGetProperty("locations", out var restricted) && Mentions(restricted, location));
                    if (!appliesHere)
                        continue;
                    var type = Str(restriction, "type");
                    locationRestricted |= string.Equals(type, "Location", StringComparison.OrdinalIgnoreCase);
                    zoneRestricted |= string.Equals(type, "Zone", StringComparison.OrdinalIgnoreCase);
                }
            }

            index.TryAdd(name, new SkuInfo(Str(sku, "family"), vCpus, locationRestricted, zoneRestricted));
        }
        return index;
    }

    private static bool Mentions(JsonElement array, string location) =>
        array.ValueKind == JsonValueKind.Array
        && array.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() is { } s && NormalizeLocation(s) == location);

    /// <summary>The usage entries by <c>name.value</c> (case-insensitive; the first entry wins).</summary>
    private static Dictionary<string, UsageRow> UsageRows(IEnumerable<JsonElement> items)
    {
        var rows = new Dictionary<string, UsageRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var usage in items)
        {
            if (usage.ValueKind != JsonValueKind.Object || !usage.TryGetProperty("name", out var name) || Str(name, "value") is not { } value)
                continue;
            rows.TryAdd(value, new UsageRow(value, Str(name, "localizedValue") ?? value, Long(usage, "currentValue"), Long(usage, "limit")));
        }
        return rows;
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s
            : null;

    private static long? Long(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt64(out var n) ? n : value.TryGetDouble(out var d) && double.IsFinite(d) ? (long)Math.Ceiling(d) : null;
        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>A capability value ("2", or a number) as a positive integer; anything else is no count.</summary>
    private static int? PositiveInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        var n = 0;
        var ok = value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out n),
            JsonValueKind.String => int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n),
            _ => false,
        };
        return ok && n > 0 ? n : null;
    }

    // ---------- payloads ----------

    private static JsonObject? ParseObject(string? json, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            problem = "the what-if result for it has no payload";
            return null;
        }
        try
        {
            if (JsonNode.Parse(json) is JsonObject parsed)
                return parsed;
            problem = "the what-if result for it is not a JSON object";
            return null;
        }
        catch (JsonException)
        {
            problem = "the what-if result for it is not valid JSON";
            return null;
        }
    }

    /// <summary>The member at <paramref name="path"/>, names matched case-insensitively.</summary>
    private static JsonNode? Node(JsonObject? obj, params string[] path)
    {
        JsonNode? current = obj;
        foreach (var name in path)
        {
            if (current is not JsonObject o)
                return null;
            current = null;
            foreach (var (key, value) in o)
            {
                if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    current = value;
                    break;
                }
            }
        }
        return current;
    }

    /// <summary>A non-empty string member; an unresolved template expression (<c>[...]</c>) is no value.</summary>
    private static string? Text(JsonObject? obj, params string[] path) =>
        Node(obj, path) is JsonValue value && value.GetValueKind() == JsonValueKind.String
        && value.GetValue<string>().Trim() is { Length: > 0 } text
        && !(text.StartsWith('[') && !text.StartsWith("[[", StringComparison.Ordinal))
            ? text
            : null;

    /// <summary>A non-negative integer member (a number or a numeric string).</summary>
    private static int? Count(JsonObject? obj, params string[] path)
    {
        if (Node(obj, path) is not JsonValue value)
            return null;
        long n = -1;
        var ok = value.GetValueKind() switch
        {
            JsonValueKind.Number => value.TryGetValue(out n),
            JsonValueKind.String => long.TryParse(value.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n),
            _ => false,
        };
        return ok && n is >= 0 and <= int.MaxValue ? (int)n : null;
    }

    private static bool? Flag(JsonObject? obj, params string[] path) =>
        Node(obj, path) is JsonValue value
            ? value.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetValue<string>(), out var b) => b,
                _ => null,
            }
            : null;

    private static string? LocationOf(JsonObject? resource) => Text(resource, "location") is { } location ? NormalizeLocation(location) : null;

    /// <summary>"West Europe" and "westeurope" are one location.</summary>
    private static string NormalizeLocation(string location) =>
        string.Concat(location.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();

    private static string? SubscriptionOf(string? resourceId) =>
        resourceId is not null && Segments(resourceId) is { Length: >= 2 } segments
        && segments[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase)
            ? segments[1]
            : null;
}

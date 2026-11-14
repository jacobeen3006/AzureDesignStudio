using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Discovery.Preflight;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// Wave B4: the quota preflight turns the what-if after payloads into vCPU and public IP demand
/// (Modify rows count only the increase), then reads Resource SKUs and the compute and network
/// usages for each location with demand. Advisory: no permission, a failed read and the time
/// limit make the affected checks Unknown with a note, and spot, unknown sizes and SKUs without
/// a vCPU count never produce a false Ok. All offline, scripted ARM.
/// </summary>
public class QuotaPreflightTests
{
    private const string Sub = "00000000-0000-0000-0000-0000000000b4";
    private const string Rg = $"/subscriptions/{Sub}/resourceGroups/rg-app";
    private const string SkusPath = $"/subscriptions/{Sub}/providers/Microsoft.Compute/skus";
    private const string DSv5 = "standardDSv5Family";
    private const string ESv5 = "standardESv5Family";
    private const string SpotNote = "Spot quota is a separate pool and is not checked.";
    private const string ForbiddenNote =
        "No permission to read quotas (needs Microsoft.Compute/locations/usages/read, Microsoft.Compute/skus/read and Microsoft.Network/locations/usages/read).";
    private const string NoVcpus = "SKU reports no vCPU count";

    private static string ComputeUsagesPath(string location) => $"/subscriptions/{Sub}/providers/Microsoft.Compute/locations/{location}/usages";
    private static string NetworkUsagesPath(string location) => $"/subscriptions/{Sub}/providers/Microsoft.Network/locations/{location}/usages";

    // ---------- fakes ----------

    private sealed record Sent(string Method, string Path, string Query)
    {
        public string PlainQuery => Uri.UnescapeDataString(Query);
    }

    /// <summary>Scripted ARM: routes by exact path (no query), case-insensitive; the route added
    /// last wins; an unrouted request answers 501, never 404.</summary>
    private sealed class QuotaArm : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Sent> _sent = new();
        private readonly Dictionary<string, Func<Sent, CancellationToken, Task<HttpResponseMessage>>> _routes = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<Sent> Sent => _sent.ToArray();

        public QuotaArm Route(string path, Func<Sent, CancellationToken, Task<HttpResponseMessage>> answer)
        {
            _routes[path] = answer;
            return this;
        }

        public QuotaArm Route(string path, string body, HttpStatusCode status = HttpStatusCode.OK) =>
            Route(path, (_, _) => Task.FromResult(Json(body, status)));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var sent = new Sent(request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query);
            _sent.Enqueue(sent);
            return _routes.TryGetValue(sent.Path, out var answer)
                ? await answer(sent, cancellationToken)
                : Json(ArmError("NoScriptedRoute", sent.Path), HttpStatusCode.NotImplemented);
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new AccessToken("fake-token", DateTimeOffset.MaxValue));
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
            => new("fake-token", DateTimeOffset.MaxValue);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string ArmError(string code, string message) =>
        JsonSerializer.Serialize(new { error = new { code, message } });

    private static QuotaPreflight Engine(QuotaArm arm) =>
        new(new ArmTransport(new HttpClient(arm), new FakeCredential(), ArmEndpointSet.AzureCloud, (_, _) => Task.CompletedTask));

    private static Task<QuotaPreflightResult> Check(QuotaArm arm, params WhatIfChange[] changes) =>
        Engine(arm).CheckAsync(Sub, changes, TimeSpan.FromMinutes(1), CancellationToken.None);

    // ---------- ARM fixtures (shapes from "Facts verified on Microsoft Learn") ----------

    private static JsonObject Sku(string name, string family, string? vCpus, string location = "westeurope", JsonArray? restrictions = null,
        string resourceType = "virtualMachines")
    {
        var capabilities = new JsonArray();
        if (vCpus is not null)
            capabilities.Add(new JsonObject { ["name"] = "vCPUs", ["value"] = vCpus });
        capabilities.Add(new JsonObject { ["name"] = "vCPUsAvailable", ["value"] = "1" });
        capabilities.Add(new JsonObject { ["name"] = "MemoryGB", ["value"] = "8" });
        return new JsonObject
        {
            ["resourceType"] = resourceType,
            ["name"] = name,
            ["tier"] = "Standard",
            ["size"] = name.StartsWith("Standard_", StringComparison.Ordinal) ? name["Standard_".Length..] : name,
            ["family"] = family,
            ["locations"] = new JsonArray { location },
            ["capabilities"] = capabilities,
            ["restrictions"] = restrictions ?? new JsonArray(),
        };
    }

    private static JsonObject Restriction(string type, string[] values, string[] locations, string[]? zones = null,
        string reasonCode = "NotAvailableForSubscription")
    {
        var info = new JsonObject { ["locations"] = new JsonArray(locations.Select(l => (JsonNode)l).ToArray()) };
        if (zones is not null)
            info["zones"] = new JsonArray(zones.Select(z => (JsonNode)z).ToArray());
        return new JsonObject
        {
            ["type"] = type,
            ["values"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()),
            ["restrictionInfo"] = info,
            ["reasonCode"] = reasonCode,
        };
    }

    private static JsonObject[] Catalog() =>
    [
        Sku("Standard_D2s_v5", DSv5, "2"),
        Sku("Standard_D4s_v5", DSv5, "4"),
        Sku("Standard_D8s_v5", DSv5, "8"),
        Sku("Standard_E4s_v5", ESv5, "4"),
        Sku("Standard_D2s_v5", DSv5, "2", "northeurope"),
        Sku("Premium_LRS", "", null, resourceType: "disks"),
    ];

    /// <summary>Answers the SKU list for the location named in <c>$filter</c>.</summary>
    private static Func<Sent, CancellationToken, Task<HttpResponseMessage>> SkuCatalog(params JsonObject[] skus) => (sent, _) =>
    {
        var location = Regex.Match(sent.PlainQuery, "location eq '([^']*)'").Groups[1].Value;
        var matching = skus.Where(s => s["locations"]!.AsArray().Any(l => l!.GetValue<string>() == location)).Select(s => s.DeepClone());
        return Task.FromResult(Json(List(matching.ToArray())));
    };

    private static JsonObject Usage(string name, string localizedValue, long current, long limit) => new()
    {
        ["name"] = new JsonObject { ["value"] = name, ["localizedValue"] = localizedValue },
        ["currentValue"] = current,
        ["limit"] = limit,
        ["unit"] = "Count",
    };

    private static string List(params JsonNode?[] items) => new JsonObject { ["value"] = new JsonArray(items) }.ToJsonString();

    private static string ComputeUsages(long coresCurrent = 10, long dsv5Current = 8, long dsv5Limit = 50, long esv5Current = 0) => List(
        Usage("cores", "Total Regional vCPUs", coresCurrent, 100),
        Usage(DSv5, "Standard DSv5 Family vCPUs", dsv5Current, dsv5Limit),
        Usage(ESv5, "Standard ESv5 Family vCPUs", esv5Current, 20));

    private static string NetworkUsages(bool standardRow = true) => standardRow
        ? List(Usage("PublicIPAddresses", "Public IP Addresses", 3, 100), Usage("StandardSkuPublicIpAddresses", "Standard Sku Public IP Addresses", 2, 100))
        : List(Usage("PublicIPAddresses", "Public IP Addresses", 3, 100));

    private static QuotaArm Arm() => new QuotaArm()
        .Route(SkusPath, SkuCatalog(Catalog()))
        .Route(ComputeUsagesPath("westeurope"), ComputeUsages())
        .Route(NetworkUsagesPath("westeurope"), NetworkUsages());

    // ---------- what-if fixtures ----------

    private static string Id(string type, string name) => $"{Rg}/providers/{type}/{name}";

    private static JsonObject Resource(string id, string type, string? location, JsonObject properties, JsonObject? sku = null)
    {
        var resource = new JsonObject
        {
            ["id"] = id,
            ["name"] = id[(id.LastIndexOf('/') + 1)..],
            ["type"] = type,
            ["apiVersion"] = "2024-07-01",
        };
        if (location is not null)
            resource["location"] = location;
        if (sku is not null)
            resource["sku"] = sku;
        resource["properties"] = properties;
        return resource;
    }

    private static string VmJson(string name, string? size, string? location = "westeurope", string? priority = null)
    {
        var properties = new JsonObject();
        if (size is not null)
            properties["hardwareProfile"] = new JsonObject { ["vmSize"] = size };
        properties["osProfile"] = new JsonObject { ["computerName"] = name, ["adminUsername"] = "azureuser" };
        if (priority is not null)
            properties["priority"] = priority;
        return Resource(Id("Microsoft.Compute/virtualMachines", name), "Microsoft.Compute/virtualMachines", location, properties).ToJsonString();
    }

    private static string VmssJson(string name, string? size, int capacity, string? priority = null, string? location = "westeurope")
    {
        var profile = new JsonObject { ["osProfile"] = new JsonObject { ["computerNamePrefix"] = name } };
        if (priority is not null)
            profile["priority"] = priority;
        var sku = size is null ? null : new JsonObject { ["name"] = size, ["tier"] = "Standard", ["capacity"] = capacity };
        return Resource(Id("Microsoft.Compute/virtualMachineScaleSets", name), "Microsoft.Compute/virtualMachineScaleSets", location,
            new JsonObject { ["orchestrationMode"] = "Uniform", ["virtualMachineProfile"] = profile }, sku).ToJsonString();
    }

    private static JsonObject Pool(string name, string? size, int count, bool autoscale = false, int? maxCount = null, string? priority = null)
    {
        var pool = new JsonObject { ["name"] = name, ["count"] = count, ["osType"] = "Linux", ["mode"] = name == "system" ? "System" : "User" };
        if (size is not null)
            pool["vmSize"] = size;
        pool["enableAutoScaling"] = autoscale;
        if (autoscale)
        {
            pool["minCount"] = count;
            pool["maxCount"] = maxCount;
        }
        if (priority is not null)
            pool["scaleSetPriority"] = priority;
        return pool;
    }

    private static string AksJson(string name, params JsonObject[] pools) =>
        Resource(Id("Microsoft.ContainerService/managedClusters", name), "Microsoft.ContainerService/managedClusters", "westeurope",
            new JsonObject { ["kubernetesVersion"] = "1.30.5", ["dnsPrefix"] = name, ["agentPoolProfiles"] = new JsonArray(pools) }).ToJsonString();

    /// <summary>A <c>managedClusters/agentPools</c> child: like the REST resource, it has no location.</summary>
    private static string AgentPoolJson(string cluster, string pool, string size, int count, bool autoscale = false, int? maxCount = null)
    {
        var properties = Pool(pool, size, count, autoscale, maxCount);
        properties.Remove("name");
        return Resource(Id("Microsoft.ContainerService/managedClusters", $"{cluster}/agentPools/{pool}"),
            "Microsoft.ContainerService/managedClusters/agentPools", null, properties).ToJsonString();
    }

    private static string IpJson(string name, string? sku = "Standard", string? location = "westeurope") =>
        Resource(Id("Microsoft.Network/publicIPAddresses", name), "Microsoft.Network/publicIPAddresses", location,
            new JsonObject { ["publicIPAllocationMethod"] = "Static", ["publicIPAddressVersion"] = "IPv4" },
            sku is null ? null : new JsonObject { ["name"] = sku, ["tier"] = "Regional" }).ToJsonString();

    private static string IdOf(string json) => JsonNode.Parse(json)!["id"]!.GetValue<string>();

    private static WhatIfChange Create(string after) => new(WhatIfChangeKind.Create, IdOf(after), null, after, 0);
    private static WhatIfChange Modify(string before, string after) => new(WhatIfChangeKind.Modify, IdOf(after), before, after, 1);

    private static QuotaFinding Find(QuotaPreflightResult result, string quotaName, string location = "westeurope") =>
        Assert.Single(result.Findings, f => f.QuotaName == quotaName && f.Location == location);

    // ---------- demand (pure) ----------

    [Fact]
    public void Demand_VmCreate_AddsOneOfItsSize()
    {
        var storage = Resource(Id("Microsoft.Storage/storageAccounts", "sa1"), "Microsoft.Storage/storageAccounts", "westeurope",
            new JsonObject(), new JsonObject { ["name"] = "Standard_LRS" }).ToJsonString();

        var demand = QuotaPreflight.DemandFrom(
        [
            Create(VmJson("vm1", "Standard_D2s_v5", "West Europe")),
            Create(VmJson("vm2", "Standard_D2s_v5")),
            Create(storage),
        ]);

        Assert.Equal([new ComputeDemand("westeurope", "Standard_D2s_v5", 2)], demand.Compute);
        Assert.Empty(demand.PublicIps);
        Assert.Empty(demand.Notes);
    }

    [Fact]
    public void Demand_VmResize_AddsNewSizeAndReleasesOld()
    {
        var demand = QuotaPreflight.DemandFrom(
        [
            Modify(VmJson("vm1", "Standard_D2s_v5"), VmJson("vm1", "Standard_D4s_v5")),
            // No size change (a tag or an extension changed): no demand.
            Modify(VmJson("vm2", "Standard_E4s_v5"), VmJson("vm2", "Standard_E4s_v5")),
        ]);

        Assert.Equal(
            [new ComputeDemand("westeurope", "Standard_D2s_v5", -1), new ComputeDemand("westeurope", "Standard_D4s_v5", 1)],
            demand.Compute);
        Assert.Empty(demand.Notes);
    }

    [Theory]
    [InlineData(2, 5, 3)]
    [InlineData(5, 2, -3)]
    [InlineData(4, 4, 0)]
    public void Demand_VmssCapacityChange_CountsOnlyTheDifference(int before, int after, int expected)
    {
        var demand = QuotaPreflight.DemandFrom([Modify(VmssJson("vmss1", "Standard_D2s_v5", before), VmssJson("vmss1", "Standard_D2s_v5", after))]);

        if (expected == 0)
            Assert.Empty(demand.Compute);
        else
            Assert.Equal([new ComputeDemand("westeurope", "Standard_D2s_v5", expected)], demand.Compute);
    }

    [Fact]
    public void Demand_VmssCreate_CountsCapacity_AndResizeMovesIt()
    {
        var demand = QuotaPreflight.DemandFrom(
        [
            Create(VmssJson("vmss1", "Standard_D2s_v5", 4)),
            Modify(VmssJson("vmss2", "Standard_D2s_v5", 3), VmssJson("vmss2", "Standard_D4s_v5", 3)),
        ]);

        Assert.Equal(
            [new ComputeDemand("westeurope", "Standard_D2s_v5", 1), new ComputeDemand("westeurope", "Standard_D4s_v5", 3)],
            demand.Compute);
    }

    [Fact]
    public void Demand_AksAutoscale_UsesMaxCount()
    {
        var demand = QuotaPreflight.DemandFrom(
        [
            Create(AksJson("aks1",
                Pool("system", "Standard_D4s_v5", 3),
                Pool("user", "Standard_D8s_v5", 2, autoscale: true, maxCount: 10))),
        ]);

        Assert.Equal(
            [new ComputeDemand("westeurope", "Standard_D4s_v5", 3), new ComputeDemand("westeurope", "Standard_D8s_v5", 10)],
            demand.Compute);
        Assert.Empty(demand.Notes);
    }

    [Fact]
    public void Demand_AksModify_SubtractsTheBeforePoolWithTheSameName()
    {
        var before = AksJson("aks1",
            Pool("user", "Standard_D8s_v5", 2, autoscale: true, maxCount: 5),
            Pool("system", "Standard_D4s_v5", 3),
            Pool("old", "Standard_D2s_v5", 4));
        var after = AksJson("aks1",
            Pool("USER", "Standard_D8s_v5", 2, autoscale: true, maxCount: 10),
            Pool("system", "Standard_D4s_v5", 3),
            Pool("new", "Standard_E4s_v5", 2));

        var demand = QuotaPreflight.DemandFrom([Modify(before, after)]);

        // user: the after maxCount 10 minus the 2 nodes that exist (never the before maxCount 5);
        // system unchanged; the removed "old" pool is not released.
        Assert.Equal(
            [new ComputeDemand("westeurope", "Standard_D8s_v5", 8), new ComputeDemand("westeurope", "Standard_E4s_v5", 2)],
            demand.Compute);
    }

    [Fact]
    public void Demand_AksModify_ReleasesOnlyTheNodesThatExist()
    {
        // Usage counts the nodes running now - the before pool's count - never its maxCount. A pool
        // autoscaling between 3 and 20 nodes set to a fixed 10 adds 7, in a cluster profile and in
        // an agentPools child alike.
        var demand = QuotaPreflight.DemandFrom(
        [
            Modify(AksJson("aks1", Pool("user", "Standard_D8s_v5", 3, autoscale: true, maxCount: 20)),
                AksJson("aks1", Pool("user", "Standard_D8s_v5", 10))),
            Modify(AgentPoolJson("aks1", "burst", "Standard_D4s_v5", 3, autoscale: true, maxCount: 20),
                AgentPoolJson("aks1", "burst", "Standard_D4s_v5", 10)),
        ]);

        Assert.Equal(
            [new ComputeDemand("westeurope", "Standard_D4s_v5", 7), new ComputeDemand("westeurope", "Standard_D8s_v5", 7)],
            demand.Compute);
        Assert.Empty(demand.Notes);
    }

    [Fact]
    public void Demand_AksModify_BeforePoolWithoutCount_ReleasesNothing()
    {
        var before = AksJson("aks1", WithoutCount(Pool("user", "Standard_D8s_v5", 3, autoscale: true, maxCount: 20)));
        var after = AksJson("aks1", Pool("user", "Standard_D8s_v5", 4));

        var demand = QuotaPreflight.DemandFrom([Modify(before, after)]);

        Assert.Equal([new ComputeDemand("westeurope", "Standard_D8s_v5", 4)], demand.Compute);
    }

    [Fact]
    public void Demand_AgentPoolsChild_UsesTheClusterLocation_AndTheCountRule()
    {
        var cluster = AksJson("aks1", Pool("system", "Standard_D4s_v5", 3));
        var orphanId = Id("Microsoft.ContainerService/managedClusters", "aks9/agentPools/x");

        var demand = QuotaPreflight.DemandFrom(
        [
            new WhatIfChange(WhatIfChangeKind.NoChange, IdOf(cluster), cluster, cluster, 0),
            Create(AgentPoolJson("aks1", "burst", "Standard_D8s_v5", 1, autoscale: true, maxCount: 6)),
            Modify(AgentPoolJson("aks1", "user", "Standard_D4s_v5", 3), AgentPoolJson("aks1", "user", "Standard_D4s_v5", 5)),
            Create(AgentPoolJson("aks9", "x", "Standard_D2s_v5", 2)),
        ]);

        Assert.Equal(
            [new ComputeDemand("westeurope", "Standard_D4s_v5", 2), new ComputeDemand("westeurope", "Standard_D8s_v5", 6)],
            demand.Compute);
        Assert.Contains(orphanId, Assert.Single(demand.Notes));
    }

    [Fact]
    public void Demand_VmVmssAndAksSpot_AreSkipped_WithOneNote()
    {
        var demand = QuotaPreflight.DemandFrom(
        [
            Create(VmJson("vm-spot", "Standard_D2s_v5", priority: "Spot")),
            Create(VmssJson("vmss-spot", "Standard_D4s_v5", 5, priority: "Spot")),
            // The older low-priority VMs draw on the same separate pool as spot.
            Create(VmssJson("vmss-low", "Standard_D4s_v5", 5, priority: "Low")),
            Create(AksJson("aks1",
                Pool("system", "Standard_D4s_v5", 2),
                Pool("spot", "Standard_D8s_v5", 1, autoscale: true, maxCount: 20, priority: "Spot"))),
        ]);

        Assert.Equal([new ComputeDemand("westeurope", "Standard_D4s_v5", 2)], demand.Compute);
        Assert.Equal([SpotNote], demand.Notes);
    }

    [Fact]
    public void Demand_MissingLocationOrSize_NoDemand_NotedByResource()
    {
        WhatIfChange[] changes =
        [
            Create(VmJson("vm-noloc", "Standard_D2s_v5", location: null)),
            Create(VmJson("vm-nosize", null)),
            Create(VmssJson("vmss-nosku", null, 3)),
            Create(IpJson("pip-noloc", location: null)),
            new(WhatIfChangeKind.Create, Id("Microsoft.Compute/virtualMachines", "vm-bad"), null, "{ not json", 0),
        ];

        var demand = QuotaPreflight.DemandFrom(changes);

        Assert.Empty(demand.Compute);
        Assert.Empty(demand.PublicIps);
        Assert.Equal(5, demand.Notes.Count);
        foreach (var change in changes)
            Assert.Single(demand.Notes, n => n.Contains(change.ResourceId!, StringComparison.Ordinal));
    }

    [Fact]
    public void Demand_DuplicateMembersAndHugeCounts_AreNotedOrCounted_NeverThrown()
    {
        // Valid JSON that a JsonObject cannot materialize: "location" twice.
        var duplicate = new WhatIfChange(WhatIfChangeKind.Create, Id("Microsoft.Compute/virtualMachines", "vm-dup"), null,
            VmJson("vm-dup", "Standard_D2s_v5").Replace("\"type\":", "\"location\":\"westeurope\",\"type\":", StringComparison.Ordinal), 0);

        var demand = QuotaPreflight.DemandFrom(
        [
            duplicate,
            Create(VmssJson("vmss-a", "Standard_D2s_v5", int.MaxValue)),
            Create(VmssJson("vmss-b", "Standard_D2s_v5", int.MaxValue)),
        ]);

        Assert.Equal([new ComputeDemand("westeurope", "Standard_D2s_v5", int.MaxValue)], demand.Compute);
        Assert.Contains(duplicate.ResourceId!, Assert.Single(demand.Notes));
    }

    [Fact]
    public async Task Check_HugeDemand_Exceeds_NeverThrows()
    {
        // Three sizes of int.MaxValue instances x int.MaxValue vCPUs: more than a long holds.
        var huge = int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var arm = Arm().Route(SkusPath, SkuCatalog(Sku("Standard_X1", DSv5, huge), Sku("Standard_X2", DSv5, huge), Sku("Standard_X3", DSv5, huge)));

        var result = await Check(arm,
            Create(VmssJson("vmss-a", "Standard_X1", int.MaxValue)),
            Create(VmssJson("vmss-b", "Standard_X2", int.MaxValue)),
            Create(VmssJson("vmss-c", "Standard_X3", int.MaxValue)));

        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f =>
        {
            Assert.Equal(QuotaStatus.Exceeds, f.Status);
            Assert.Equal(long.MaxValue, f.Requested);
        });
    }

    [Fact]
    public void Demand_PublicIp_CreateCountsItsSku_DefaultStandard()
    {
        var demand = QuotaPreflight.DemandFrom(
        [
            Create(IpJson("pip1", "Standard")),
            Create(IpJson("pip2", sku: null)),
            Create(IpJson("pip3", "Basic")),
            Create(IpJson("pip4", "standard", "North Europe")),
            Modify(IpJson("pip5", "Standard"), IpJson("pip5", "Standard")),
        ]);

        Assert.Equal(
            [new IpDemand("northeurope", "Standard", 1), new IpDemand("westeurope", "Basic", 1), new IpDemand("westeurope", "Standard", 2)],
            demand.PublicIps);
        Assert.Empty(demand.Compute);
    }

    [Fact]
    public void Demand_SummedPerLocationAndSize_ZeroDropped_Ordered()
    {
        var demand = QuotaPreflight.DemandFrom(
        [
            Create(VmJson("vm-e", "Standard_E4s_v5")),
            Create(VmJson("vm-b", "standard_d2s_v5")),
            Modify(VmJson("vm-d", "Standard_E4s_v5"), VmJson("vm-d", "Standard_D2s_v5")),
            Create(VmJson("vm-c", "Standard_E4s_v5", "northeurope")),
            Create(VmJson("vm-a", "Standard_D2s_v5")),
            new WhatIfChange(WhatIfChangeKind.Delete, Id("Microsoft.Compute/virtualMachines", "vm-f"), VmJson("vm-f", "Standard_D2s_v5"), null, 0),
        ]);

        // E4s_v5 in westeurope: +1 (vm-e) - 1 (vm-d) = 0, dropped. The first id's casing is kept.
        Assert.Equal(
            [new ComputeDemand("northeurope", "Standard_E4s_v5", 1), new ComputeDemand("westeurope", "Standard_D2s_v5", 3)],
            demand.Compute);
    }

    [Fact]
    public void Demand_AppServiceContainerAppsAndInstances_AreNoted()
    {
        var plan = Resource(Id("Microsoft.Web/serverfarms", "plan1"), "Microsoft.Web/serverfarms", "westeurope", new JsonObject(),
            new JsonObject { ["name"] = "P1v3", ["capacity"] = 3 }).ToJsonString();
        var group = Resource(Id("Microsoft.ContainerInstance/containerGroups", "aci1"), "Microsoft.ContainerInstance/containerGroups", "westeurope",
            new JsonObject()).ToJsonString();
        var app = Resource(Id("Microsoft.App/containerApps", "app1"), "Microsoft.App/containerApps", "westeurope", new JsonObject()).ToJsonString();

        var demand = QuotaPreflight.DemandFrom([Create(plan), Create(group), Create(app)]);

        Assert.Empty(demand.Compute);
        Assert.Equal(["Quota for App Service plans, Container Apps and Container Instances is not checked."], demand.Notes);
    }

    // ---------- check ----------

    [Fact]
    public async Task Check_FamilyExceeds()
    {
        var arm = Arm().Route(ComputeUsagesPath("westeurope"), ComputeUsages(dsv5Current: 44));

        var result = await Check(arm, Create(VmJson("vm1", "Standard_D4s_v5")), Create(VmJson("vm2", "Standard_D4s_v5")));

        Assert.Equal(
            [
                new QuotaFinding("westeurope", DSv5, "Standard DSv5 Family vCPUs", 44, 50, 8, QuotaStatus.Exceeds, "Standard_D4s_v5", null),
                new QuotaFinding("westeurope", "cores", "Total Regional vCPUs", 10, 100, 8, QuotaStatus.Ok, null, null),
            ],
            result.Findings);
        Assert.Empty(result.Notes);
    }

    [Theory]
    [InlineData(85, QuotaStatus.Ok)]        // 89 of 100
    [InlineData(86, QuotaStatus.NearLimit)] // 90 of 100: the ratio is inclusive
    [InlineData(96, QuotaStatus.NearLimit)] // exactly the limit still fits
    [InlineData(97, QuotaStatus.Exceeds)]   // 101 of 100
    public async Task Check_RegionalCoresNearLimit(long coresCurrent, QuotaStatus expected)
    {
        var arm = Arm().Route(ComputeUsagesPath("westeurope"), ComputeUsages(coresCurrent: coresCurrent));

        var result = await Check(arm, Create(VmJson("vm1", "Standard_D4s_v5")));

        var cores = Find(result, "cores");
        Assert.Equal(expected, cores.Status);
        Assert.Equal(4, cores.Requested);
        Assert.Equal(coresCurrent, cores.Current);
        Assert.Equal(QuotaStatus.Ok, Find(result, DSv5).Status);
    }

    [Fact]
    public async Task Check_ResizeWithinFamily_RequestsOnlyTheDifference()
    {
        // Counting the new size in full (4) would exceed 50; the difference (2) does not.
        var arm = Arm().Route(ComputeUsagesPath("westeurope"), ComputeUsages(dsv5Current: 47));

        var result = await Check(arm, Modify(VmJson("vm1", "Standard_D2s_v5"), VmJson("vm1", "Standard_D4s_v5")));

        var family = Find(result, DSv5);
        Assert.Equal(2, family.Requested);
        Assert.Equal(QuotaStatus.NearLimit, family.Status);
        Assert.Equal("Standard_D4s_v5", family.VmSize);
        Assert.Equal(2, Find(result, "cores").Requested);
        Assert.Equal(2, result.Findings.Count);
    }

    [Fact]
    public async Task Check_ResizeAcrossFamilies_ReleasesOnlyFromTheRegionalTotal()
    {
        var result = await Check(Arm(), Modify(VmJson("vm1", "Standard_D4s_v5"), VmJson("vm1", "Standard_E4s_v5")));

        Assert.Equal(
            [
                new QuotaFinding("westeurope", "cores", "Total Regional vCPUs", 10, 100, 0, QuotaStatus.Ok, null, null),
                new QuotaFinding("westeurope", ESv5, "Standard ESv5 Family vCPUs", 0, 20, 4, QuotaStatus.Ok, "Standard_E4s_v5", null),
            ],
            result.Findings);
    }

    [Fact]
    public async Task Check_AutoscaledPoolRedeploy_ReleasesNoQuotaItNeverUsed()
    {
        // A count-only redeploy over a pool that autoscales between 3 and 20 nodes changes nothing
        // in DSv5. Releasing the before maxCount (17 x 8 vCPUs) would hide the new E16s_v5 VM from
        // the regional total, where 85 + 16 of 100 exceeds it.
        var arm = Arm()
            .Route(SkusPath, SkuCatalog([.. Catalog(), Sku("Standard_E16s_v5", ESv5, "16")]))
            .Route(ComputeUsagesPath("westeurope"), ComputeUsages(coresCurrent: 85));

        var result = await Check(arm,
            Modify(AksJson("aks1", Pool("user", "Standard_D8s_v5", 3, autoscale: true, maxCount: 20)),
                AksJson("aks1", Pool("user", "Standard_D8s_v5", 3))),
            Create(VmJson("vm1", "Standard_E16s_v5")));

        var cores = Find(result, "cores");
        Assert.Equal(QuotaStatus.Exceeds, cores.Status);
        Assert.Equal(16, cores.Requested);
        Assert.DoesNotContain(result.Findings, f => f.QuotaName == DSv5);
    }

    [Fact]
    public async Task Check_SizeNotOffered_IsSkuUnavailable()
    {
        // The same name as another resource type is not a VM size.
        var arm = Arm().Route(SkusPath, SkuCatalog([.. Catalog(), Sku("Standard_NV999", "standardNVFamily", "999", resourceType: "disks")]));

        var result = await Check(arm, Create(VmJson("vm1", "Standard_NV999")), Create(VmJson("vm2", "Standard_D2s_v5")));

        Assert.Equal(
            new[] { (QuotaStatus.SkuUnavailable, "Standard_NV999"), (QuotaStatus.Unknown, "cores"), (QuotaStatus.Ok, DSv5) },
            result.Findings.Select(f => (f.Status, f.QuotaName)));
        var unavailable = result.Findings[0];
        Assert.Equal("Standard_NV999", unavailable.VmSize);
        Assert.Equal("not offered in westeurope", unavailable.Detail);
        // Its vCPU count is unknown, so the regional total cannot be Ok.
        var cores = Find(result, "cores");
        Assert.Contains("Standard_NV999", cores.Detail);
        Assert.Equal(10, cores.Current);
        Assert.Equal(100, cores.Limit);
    }

    [Fact]
    public async Task Check_LocationRestriction_IsSkuUnavailable()
    {
        var arm = Arm().Route(SkusPath, SkuCatalog(
            Sku("Standard_D8s_v5", DSv5, "8", restrictions: [Restriction("Location", ["westeurope"], ["westeurope"])]),
            Sku("Standard_E4s_v5", ESv5, "4", restrictions: [Restriction("Location", [], ["WestEurope"], reasonCode: "QuotaId")]),
            Sku("Standard_D4s_v5", DSv5, "4", restrictions: [Restriction("Location", ["eastus"], ["eastus"])]),
            Sku("Standard_D2s_v5", DSv5, "2", restrictions: [Restriction("Zone", ["westeurope"], ["westeurope"], zones: ["2"])])));

        var result = await Check(arm,
            Create(VmJson("vm8", "Standard_D8s_v5")),
            Create(VmJson("vme", "Standard_E4s_v5")),
            Create(VmJson("vm4", "Standard_D4s_v5")),
            Create(VmJson("vm2", "Standard_D2s_v5")));

        var unavailable = result.Findings.Where(f => f.Status == QuotaStatus.SkuUnavailable).ToList();
        Assert.Equal(["Standard_D8s_v5", "Standard_E4s_v5"], unavailable.Select(f => f.VmSize));
        Assert.All(unavailable, f => Assert.Equal("not available to this subscription in westeurope", f.Detail));
        Assert.Equal([8L, 4L], unavailable.Select(f => f.Requested));
        // Restricted sizes still count against their family and the regional total.
        Assert.Equal(14, Find(result, DSv5).Requested);
        Assert.Equal(18, Find(result, "cores").Requested);
        var zoneNote = Assert.Single(result.Notes);
        Assert.Contains("Standard_D2s_v5", zoneNote);
        Assert.Contains("zones are not checked", zoneNote);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("two")]
    [InlineData("0")]
    public async Task Check_MissingVcpus_IsUnknown(string? vCpus)
    {
        var arm = Arm().Route(SkusPath, SkuCatalog(Sku("Standard_D2s_v5", DSv5, vCpus)));

        var result = await Check(arm, Create(VmJson("vm1", "Standard_D2s_v5")));

        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f =>
        {
            Assert.Equal(QuotaStatus.Unknown, f.Status);
            Assert.Equal(NoVcpus, f.Detail);
        });
        Assert.Equal(8, Find(result, DSv5).Current);
    }

    [Fact]
    public async Task Check_UncountedResource_MakesItsChecksUnknown()
    {
        // The scale set's instances draw on DSv5 and the regional total like the VM next to it:
        // 8 + 2 of 50 counted without them must not read as Ok.
        var vmss = UncountedCapacity(VmssJson("vmss1", "Standard_D2s_v5", 3));
        var vmssId = IdOf(vmss);

        var result = await Check(Arm(), Create(vmss), Create(VmJson("vm1", "Standard_D2s_v5")));

        Assert.Equal(
            [
                new QuotaFinding("westeurope", "cores", "Total Regional vCPUs", 10, 100, 2, QuotaStatus.Unknown, null, $"{vmssId} is not counted"),
                new QuotaFinding("westeurope", DSv5, "Standard DSv5 Family vCPUs", 8, 50, 2, QuotaStatus.Unknown, "Standard_D2s_v5",
                    $"{vmssId} is not counted"),
            ],
            result.Findings);
        Assert.Equal([$"{vmssId} is not counted for quota: its instance count is unknown."], result.Notes);
    }

    [Theory]
    [InlineData("vm-size", false)]
    [InlineData("vmss-size", false)]
    [InlineData("vmss-capacity", true)]
    [InlineData("aks-size", false)]
    [InlineData("aks-count", true)]
    public async Task Check_UncountedResourceAlone_StillReadsItsLocation(string uncounted, bool sizeKnown)
    {
        var after = uncounted switch
        {
            "vm-size" => VmJson("vm1", null),
            "vmss-size" => VmssJson("vmss1", null, 3),
            "vmss-capacity" => UncountedCapacity(VmssJson("vmss1", "Standard_D2s_v5", 3)),
            "aks-size" => AksJson("aks1", Pool("system", null, 3)),
            // count omitted (the newer pool shapes): no node count.
            _ => AksJson("aks1", WithoutCount(Pool("system", "Standard_D2s_v5", 3))),
        };
        var id = IdOf(after);
        var arm = Arm();

        var result = await Check(arm, Create(after));

        // Nothing else has demand in westeurope, and its SKU list and compute usages are still read.
        Assert.Equal([ComputeUsagesPath("westeurope"), SkusPath], arm.Sent.Select(s => s.Path).Order(StringComparer.Ordinal));
        var cores = new QuotaFinding("westeurope", "cores", "Total Regional vCPUs", 10, 100, 0, QuotaStatus.Unknown, null, $"{id} is not counted");
        // Without a size the family is unknown; the regional total still cannot be Ok.
        QuotaFinding[] expected = sizeKnown
            ? [cores, new QuotaFinding("westeurope", DSv5, "Standard DSv5 Family vCPUs", 8, 50, 0, QuotaStatus.Unknown, "Standard_D2s_v5", $"{id} is not counted")]
            : [cores];
        Assert.Equal(expected, result.Findings);
        Assert.Contains(id, Assert.Single(result.Notes));
    }

    /// <summary>The scale set with an instance count what-if left unresolved.</summary>
    private static string UncountedCapacity(string vmssJson)
    {
        var vmss = JsonNode.Parse(vmssJson)!.AsObject();
        vmss["sku"]!["capacity"] = "[parameters('instanceCount')]";
        return vmss.ToJsonString();
    }

    private static JsonObject WithoutCount(JsonObject pool)
    {
        pool.Remove("count");
        return pool;
    }

    [Fact]
    public async Task Check_NoUsageRow_IsUnknown()
    {
        var arm = Arm().Route(ComputeUsagesPath("westeurope"), List(Usage("cores", "Total Regional vCPUs", 10, 100)));

        var result = await Check(arm, Create(VmJson("vm1", "Standard_D2s_v5")));

        Assert.Equal(
            new QuotaFinding("westeurope", DSv5, DSv5, 0, 0, 2, QuotaStatus.Unknown, "Standard_D2s_v5", $"no usage entry named '{DSv5}'"),
            Find(result, DSv5));
        Assert.Equal(QuotaStatus.Ok, Find(result, "cores").Status);
    }

    [Fact]
    public async Task Check_StandardIpFallsBackToPublicIPAddresses()
    {
        WhatIfChange[] ips = [Create(IpJson("pip1")), Create(IpJson("pip2")), Create(IpJson("pip3", "Basic"))];

        var withoutStandardRow = Arm().Route(NetworkUsagesPath("westeurope"), NetworkUsages(standardRow: false));
        var fallback = await Check(withoutStandardRow, ips);
        var withStandardRow = await Check(Arm(), ips);

        Assert.Equal(
            [new QuotaFinding("westeurope", "PublicIPAddresses", "Public IP Addresses", 3, 100, 3, QuotaStatus.Ok, null, null)],
            fallback.Findings);
        Assert.Equal(
            [
                new QuotaFinding("westeurope", "PublicIPAddresses", "Public IP Addresses", 3, 100, 1, QuotaStatus.Ok, null, null),
                new QuotaFinding("westeurope", "StandardSkuPublicIpAddresses", "Standard Sku Public IP Addresses", 2, 100, 2, QuotaStatus.Ok, null, null),
            ],
            withStandardRow.Findings);
        // Public IPs alone read only the network usages.
        Assert.All(withoutStandardRow.Sent, s => Assert.Equal(NetworkUsagesPath("westeurope"), s.Path));
    }

    [Fact]
    public async Task Check_Forbidden_AllUnknownWithNote()
    {
        var forbidden = ArmError("AuthorizationFailed", "The client does not have authorization to perform action 'Microsoft.Compute/skus/read'.");
        var arm = new QuotaArm()
            .Route(SkusPath, forbidden, HttpStatusCode.Forbidden)
            .Route(ComputeUsagesPath("westeurope"), forbidden, HttpStatusCode.Forbidden)
            .Route(NetworkUsagesPath("westeurope"), forbidden, HttpStatusCode.Forbidden);

        var result = await Check(arm, Create(VmJson("vm1", "Standard_D2s_v5")), Create(IpJson("pip1")));

        Assert.Equal(3, arm.Sent.Count);
        // Without the SKU list the size is checked under its own name.
        Assert.Equal(["StandardSkuPublicIpAddresses", "Standard_D2s_v5", "cores"], result.Findings.Select(f => f.QuotaName).Order(StringComparer.Ordinal));
        Assert.All(result.Findings, f =>
        {
            Assert.Equal(QuotaStatus.Unknown, f.Status);
            Assert.Contains("no permission", f.Detail);
        });
        Assert.Equal([ForbiddenNote], result.Notes);
    }

    [Fact]
    public async Task Check_FollowsNextLink()
    {
        const string root = $"https://management.azure.com/subscriptions/{Sub}/providers";
        var arm = Arm()
            .Route(SkusPath, (sent, _) => Task.FromResult(Json(sent.PlainQuery.Contains("$skiptoken=2", StringComparison.Ordinal)
                ? List(Sku("Standard_D4s_v5", DSv5, "4"))
                : new JsonObject
                {
                    ["value"] = new JsonArray(Sku("Standard_E4s_v5", ESv5, "4")),
                    ["nextLink"] = $"{root}/Microsoft.Compute/skus?api-version=2021-07-01&$filter=location%20eq%20%27westeurope%27&$skiptoken=2",
                }.ToJsonString())))
            .Route(ComputeUsagesPath("westeurope"), (sent, _) => Task.FromResult(Json(sent.PlainQuery.Contains("$skiptoken=2", StringComparison.Ordinal)
                ? List(Usage(DSv5, "Standard DSv5 Family vCPUs", 44, 50))
                : new JsonObject
                {
                    ["value"] = new JsonArray(Usage("cores", "Total Regional vCPUs", 10, 100)),
                    ["nextLink"] = $"{root}/Microsoft.Compute/locations/westeurope/usages?api-version=2026-04-01&$skiptoken=2",
                }.ToJsonString())));

        var result = await Check(arm, Create(VmJson("vm1", "Standard_D4s_v5")), Create(VmJson("vm2", "Standard_D4s_v5")));

        Assert.Equal(4, arm.Sent.Count);
        var family = Find(result, DSv5);
        Assert.Equal(QuotaStatus.Exceeds, family.Status);
        Assert.Equal(44, family.Current);
        Assert.Equal(8, family.Requested);
        Assert.Equal(QuotaStatus.Ok, Find(result, "cores").Status);
    }

    [Fact]
    public async Task Check_NoDemand_MakesNoCalls()
    {
        var arm = Arm();
        var storage = Resource(Id("Microsoft.Storage/storageAccounts", "sa1"), "Microsoft.Storage/storageAccounts", "westeurope", new JsonObject()).ToJsonString();

        var result = await Check(arm,
            Create(storage),
            new WhatIfChange(WhatIfChangeKind.Delete, Id("Microsoft.Compute/virtualMachines", "vm-old"), VmJson("vm-old", "Standard_D2s_v5"), null, 0),
            Create(VmJson("vm-spot", "Standard_D2s_v5", priority: "Spot")),
            Modify(VmssJson("vmss1", "Standard_D2s_v5", 5), VmssJson("vmss1", "Standard_D2s_v5", 2)),
            Modify(IpJson("pip1"), IpJson("pip1")));

        Assert.Empty(arm.Sent);
        Assert.Empty(result.Findings);
        Assert.Equal([SpotNote], result.Notes);
    }

    [Fact]
    public async Task Check_BudgetExpires_RestUnknown()
    {
        var arm = Arm().Route(NetworkUsagesPath("westeurope"), async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token); // only the deadline ends this one
            return Json(NetworkUsages());
        });
        var clock = Stopwatch.StartNew();

        var result = await Engine(arm).CheckAsync(Sub, [Create(VmJson("vm1", "Standard_D2s_v5")), Create(IpJson("pip1"))],
            TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
        Assert.Equal(QuotaStatus.Ok, Find(result, DSv5).Status);
        Assert.Equal(QuotaStatus.Ok, Find(result, "cores").Status);
        var ip = Find(result, "StandardSkuPublicIpAddresses");
        Assert.Equal(QuotaStatus.Unknown, ip.Status);
        Assert.Equal(1, ip.Requested);
        Assert.Contains("time limit", ip.Detail);
        Assert.Equal(["Quota check stopped at the time limit: 1 of 3 quota reads did not finish."], result.Notes);
    }

    [Fact]
    public async Task Check_ZeroBudget_ReadsNothing_AllUnknown()
    {
        var arm = Arm();

        var result = await Engine(arm).CheckAsync(Sub, [Create(VmJson("vm1", "Standard_D2s_v5")), Create(IpJson("pip1"))],
            TimeSpan.Zero, CancellationToken.None);

        Assert.Empty(arm.Sent);
        Assert.Equal(3, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(QuotaStatus.Unknown, f.Status));
        Assert.Equal(["Quota check stopped at the time limit: 3 of 3 quota reads did not finish."], result.Notes);
    }

    [Theory]
    [InlineData(500, """{"error":{"code":"InternalServerError","message":"boom"}}""", "InternalServerError")]
    [InlineData(429, "", "Throttled")]
    [InlineData(200, "this is not json", "MalformedResponse")]
    public async Task Check_UsageReadFails_KeepsWhatWasRead_RestUnknownWithCode(int status, string body, string code)
    {
        var arm = Arm().Route(ComputeUsagesPath("westeurope"), body, (HttpStatusCode)status);

        var result = await Check(arm, Create(VmJson("vm1", "Standard_NV999")), Create(VmJson("vm2", "Standard_D2s_v5")));

        Assert.Equal(
            new[] { (QuotaStatus.SkuUnavailable, "Standard_NV999"), (QuotaStatus.Unknown, "cores"), (QuotaStatus.Unknown, DSv5) },
            result.Findings.Select(f => (f.Status, f.QuotaName)));
        Assert.All(result.Findings.Skip(1), f => Assert.Contains(code, f.Detail));
        Assert.Equal(2, Find(result, DSv5).Requested);
        var note = Assert.Single(result.Notes);
        Assert.Contains("westeurope", note);
        Assert.Contains(code, note);
    }

    [Fact]
    public async Task Check_ReadsEachSourceOncePerLocation_WithTheDocumentedApiVersions()
    {
        var arm = Arm()
            .Route(ComputeUsagesPath("northeurope"), ComputeUsages())
            .Route(NetworkUsagesPath("northeurope"), NetworkUsages());

        var result = await Check(arm,
            Create(VmJson("vm1", "Standard_D2s_v5")),
            Create(VmJson("vm2", "Standard_D2s_v5", "North Europe")),
            Create(VmJson("vm3", "Standard_D2s_v5", "northeurope")),
            Create(IpJson("pip1", location: "northeurope")));

        Assert.All(arm.Sent, s => Assert.Equal("GET", s.Method));
        Assert.Equal(
            new[]
            {
                $"{ComputeUsagesPath("northeurope")}?api-version=2026-04-01",
                $"{ComputeUsagesPath("westeurope")}?api-version=2026-04-01",
                $"{SkusPath}?api-version=2021-07-01&$filter=location eq 'northeurope'",
                $"{SkusPath}?api-version=2021-07-01&$filter=location eq 'westeurope'",
                $"{NetworkUsagesPath("northeurope")}?api-version=2026-01-01",
            },
            arm.Sent.Select(s => s.Path + s.PlainQuery).Order(StringComparer.Ordinal));
        Assert.Equal(4, Find(result, DSv5, "northeurope").Requested);
        Assert.Equal(2, Find(result, DSv5).Requested);
        Assert.All(result.Findings, f => Assert.Equal(QuotaStatus.Ok, f.Status));
    }

    [Fact]
    public async Task Check_Findings_OrderedByStatus_ThenLocation_ThenQuotaName()
    {
        var arm = Arm()
            .Route(ComputeUsagesPath("westeurope"), ComputeUsages(dsv5Current: 49, esv5Current: 15))
            .Route(ComputeUsagesPath("northeurope"), ComputeUsages());

        var result = await Check(arm,
            Create(IpJson("pip1")),
            Create(VmJson("vm-ne", "Standard_D2s_v5", "northeurope")),
            Create(VmJson("vm-e", "Standard_E4s_v5")),
            Create(VmJson("vm-x", "Standard_NV999")),
            Create(VmJson("vm-d", "Standard_D2s_v5")));

        Assert.Equal(
            new[]
            {
                (QuotaStatus.SkuUnavailable, "westeurope", "Standard_NV999"),
                (QuotaStatus.Exceeds, "westeurope", DSv5),
                (QuotaStatus.NearLimit, "westeurope", ESv5),
                (QuotaStatus.Unknown, "westeurope", "cores"),
                (QuotaStatus.Ok, "northeurope", "cores"),
                (QuotaStatus.Ok, "northeurope", DSv5),
                (QuotaStatus.Ok, "westeurope", "StandardSkuPublicIpAddresses"),
            },
            result.Findings.Select(f => (f.Status, f.Location, f.QuotaName)));
    }

    [Fact]
    public async Task Check_ResourcesInOtherSubscriptions_AreNotCounted()
    {
        // A nested deployment can change another subscription: its quota is not this one's.
        const string otherRg = "/subscriptions/00000000-0000-0000-0000-0000000000ff/resourceGroups/rg-other";
        var arm = Arm();

        var result = await Check(arm,
            Create(VmJson("vm1", "Standard_D2s_v5")),
            Create(VmJson("vm-other", "Standard_D8s_v5").Replace(Rg, otherRg, StringComparison.Ordinal)),
            Create(IpJson("pip-other").Replace(Rg, otherRg, StringComparison.Ordinal)));

        Assert.Equal(2, Find(result, DSv5).Requested);
        Assert.Equal(2, Find(result, "cores").Requested);
        Assert.Equal(2, result.Findings.Count);
        Assert.DoesNotContain(arm.Sent, s => s.Path.Contains("Microsoft.Network", StringComparison.Ordinal));
        Assert.Equal(
            [$"Quota is checked only in subscription {Sub}; resources this deployment changes in other subscriptions are not counted."],
            result.Notes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Check_NoSubscription_ReadsNothing_WithNote(string subscriptionId)
    {
        var arm = Arm();

        var result = await Engine(arm).CheckAsync(subscriptionId, [Create(VmJson("vm1", "Standard_D2s_v5"))], TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.Empty(arm.Sent);
        Assert.Empty(result.Findings);
        Assert.Equal(["Quota was not checked: the deployment names no subscription."], result.Notes);
    }

    [Fact]
    public async Task Check_CallerCancelled_Propagates()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arm = Arm().Route(ComputeUsagesPath("westeurope"), async (_, token) =>
        {
            arrived.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Json(ComputeUsages());
        });
        using var cts = new CancellationTokenSource();

        var check = Engine(arm).CheckAsync(Sub, [Create(VmJson("vm1", "Standard_D2s_v5"))], TimeSpan.FromMinutes(5), cts.Token);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public void Caveats_SayQuotaIsNotCapacity()
    {
        Assert.Equal(6, QuotaPreflight.Caveats.Count);
        Assert.StartsWith("Quota is not capacity", QuotaPreflight.Caveats[0], StringComparison.Ordinal);
        Assert.Contains(QuotaPreflight.Caveats, c => c.Contains("per subscription and per region", StringComparison.Ordinal));
        Assert.Contains(QuotaPreflight.Caveats, c => c.Contains("AKS upgrades", StringComparison.Ordinal));
        Assert.Contains(QuotaPreflight.Caveats, c => c.Contains("may check a new one before they are freed", StringComparison.Ordinal));
        Assert.Contains(QuotaPreflight.Caveats, c => c.Contains("Resource SKUs family name", StringComparison.Ordinal));
        Assert.Contains("Only the previewed batch is counted.", QuotaPreflight.Caveats);
    }
}

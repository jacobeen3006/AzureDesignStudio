using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.Cost;
using AzureDesignStudio.Discovery.Decommission;
using AzureDesignStudio.Discovery.Drift;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.ResourceGraph;
using AzureDesignStudio.Discovery.Scan;
using AzureDesignStudio.Discovery.Unmanaged;
using AzureDesignStudio.Workspace;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>Regression tests for the second engine pass: drift blind spots, scan resume
/// identity, cost paging, lock levels and case-insensitive workspace keys. Offline.</summary>
public class EngineRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ads-eng-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceDb _workspace;

    public EngineRegressionTests()
    {
        Directory.CreateDirectory(_directory);
        _workspace = new WorkspaceDb(Path.Combine(_directory, "workspace.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new AccessToken("fake-token", DateTimeOffset.MaxValue));
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
            => new("fake-token", DateTimeOffset.MaxValue);
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, string> responder) : HttpMessageHandler
    {
        public List<string> Uris { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!.ToString());
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responder(request, Uris.Count - 1), Encoding.UTF8, "application/json"),
            };
        }
    }

    // --- drift ---

    private const string Nsg = "/subscriptions/s1/resourcegroups/rg1/providers/microsoft.network/networksecuritygroups/nsg1";

    private static InventoryItem Item(string propertiesJson, Dictionary<string, string>? tags = null) =>
        new(Nsg, "nsg1", "microsoft.network/networksecuritygroups", "westeurope", "rg1", "s1",
            tags ?? new Dictionary<string, string>(), JsonDocument.Parse(propertiesJson).RootElement.Clone());

    private static IReadOnlyList<PropertyDiff> Diffs(InventoryItem before, InventoryItem after) =>
        new ScanDiffer().Diff([before], [after]).Drift.SelectMany(d => d.Properties).ToList();

    // Empty containers used to produce no leaf at all, so these changes were invisible.
    [Theory]
    [InlineData("""{"securityRules":[]}""", """{}""")]                              // an empty list removed
    [InlineData("""{"securityRules":[]}""", """{"securityRules":{}}""")]            // list became an object
    [InlineData("""{"identity":{}}""", """{"identity":null}""")]
    [InlineData("""{"securityRules":[]}""", """{"securityRules":[{"name":"r"}]}""")] // the empty side is reported too
    public void ChangesInvolvingEmptyContainersAreReported(string before, string after)
    {
        var diffs = Diffs(Item(before), Item(after));

        Assert.NotEmpty(diffs);
        Assert.Contains(diffs, d => d.PreviousJson is "[]" or "{}");
    }

    [Fact]
    public void ReorderedKeysAreNotDrift()
    {
        Assert.Empty(Diffs(Item("""{"a":1,"b":{"x":[],"y":{}}}"""), Item("""{"b":{"y":{},"x":[]},"a":1}""")));
    }

    [Fact]
    public void RecasingATagKeyIsNotDriftButChangingItsValueIs()
    {
        var item = Item("{}", new Dictionary<string, string> { ["Environment"] = "prod" });

        Assert.Empty(Diffs(item, Item("{}", new Dictionary<string, string> { ["environment"] = "prod" })));
        var changed = Assert.Single(Diffs(item, Item("{}", new Dictionary<string, string> { ["ENVIRONMENT"] = "dev" })));
        Assert.Equal("tags.environment", changed.Path);
    }

    // --- scan resume ---

    private sealed class NullSink : IScanSink
    {
        public Task ScanStartedAsync(ScanRequest request, ScanCheckpoint checkpoint, bool isResume, CancellationToken ct) => Task.CompletedTask;
        public Task PagePersistedAsync(string queryKey, IReadOnlyList<InventoryItem> page, ScanCheckpoint checkpoint, CancellationToken ct) => Task.CompletedTask;
        public Task ScanFinishedAsync(string status, ScanCheckpoint checkpoint, CancellationToken ct) => Task.CompletedTask;
    }

    private static (ScanEngine Engine, ScriptedHandler Handler) Engine(int groupSize = 1)
    {
        var handler = new ScriptedHandler((_, _) => """{"data":[],"count":0,"totalRecords":0,"resultTruncated":"false"}""");
        var graph = new ResourceGraphClient(new HttpClient(handler), new FakeCredential(), null, (_, _) => Task.CompletedTask);
        return (new ScanEngine(graph, groupSize), handler);
    }

    private static ScanRequest Request(params string[] subs) => new("tenant", "SubscriptionSet", subs);

    [Fact]
    public async Task ResumeWithADifferentSubscriptionSetIsRefused()
    {
        var (engine, handler) = Engine();
        var first = await engine.RunAsync(Request("a", "b"), new NullSink());
        var saved = ScanCheckpoint.FromJson(first.ToJson())!;
        var before = handler.Uris.Count;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RunAsync(Request("a", "c"), new NullSink(), resume: saved));

        Assert.Contains("different set of subscriptions", error.Message);
        Assert.Equal(before, handler.Uris.Count); // nothing was queried
    }

    [Fact]
    public async Task ResumeWithTheSameSubscriptionsInAnotherOrderSkipsEverythingAlreadyDone()
    {
        var (engine, handler) = Engine(groupSize: 2);
        var first = await engine.RunAsync(Request("a", "b", "c", "d"), new NullSink());
        var before = handler.Uris.Count;

        var resumed = await engine.RunAsync(Request("D", "c", "B", "a"), new NullSink(), resume: ScanCheckpoint.FromJson(first.ToJson()));

        Assert.Equal(before, handler.Uris.Count); // same groups whatever the input order
        Assert.Equal(2, resumed.CompletedQueries.Count);
    }

    [Fact]
    public async Task CheckpointWrittenBeforeRequestKeysExistedStillResumes()
    {
        var (engine, _) = Engine();
        var legacy = ScanCheckpoint.FromJson("""{"scanId":"x","completedQueries":[],"failedQueries":{},"rows":0}""")!;

        var resumed = await engine.RunAsync(Request("a"), new NullSink(), resume: legacy);

        Assert.Single(resumed.CompletedQueries);
        Assert.NotNull(resumed.RequestKey);
    }

    // --- cost paging ---

    private static string CostPage(string resource, decimal cost, string? nextLink) =>
        JsonSerializer.Serialize(new
        {
            properties = new
            {
                nextLink,
                columns = new object[] { new { name = "Cost", type = "Number" }, new { name = "ResourceId", type = "String" }, new { name = "Currency", type = "String" } },
                rows = new object[] { new object[] { cost, resource, "EUR" } },
            },
        });

    [Fact]
    public async Task ActualSpendFollowsNextLinkAndAddsEveryPage()
    {
        const string next = "https://management.azure.com/subscriptions/s1/providers/Microsoft.CostManagement/query?api-version=2023-08-01&$skiptoken=p2";
        var handler = new ScriptedHandler((_, call) => call == 0 ? CostPage("/subscriptions/s1/r/one", 10m, next) : CostPage("/subscriptions/s1/r/two", 32.5m, null));
        var client = new ActualSpendClient(new ArmTransport(new HttpClient(handler), new FakeCredential(), ArmEndpointSet.AzureCloud, (_, _) => Task.CompletedTask));

        var result = await client.QueryAsync("/subscriptions/s1", DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow);

        Assert.Equal(2, handler.Uris.Count);
        Assert.Equal(next, handler.Uris[1]);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]); // the same query body goes to the next link
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(42.5m, result.Rows.Sum(r => r.Cost));
    }

    [Fact]
    public async Task ActualSpendPagingThatLoopsFailsInsteadOfSpinning()
    {
        const string self = "https://management.azure.com/subscriptions/s1/providers/Microsoft.CostManagement/query?api-version=2023-08-01&$skiptoken=same";
        var handler = new ScriptedHandler((_, _) => CostPage("/subscriptions/s1/r/one", 1m, self));
        var client = new ActualSpendClient(new ArmTransport(new HttpClient(handler), new FakeCredential(), ArmEndpointSet.AzureCloud, (_, _) => Task.CompletedTask));

        var error = await Assert.ThrowsAsync<ResourceGraphException>(
            () => client.QueryAsync("/subscriptions/s1", DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow));

        Assert.Equal("PagingLoop", error.Code);
    }

    // --- locks ---

    [Fact]
    public void ReadOnlyLockBlocksTheDeleteLikeCanNotDelete()
    {
        const string vm = "/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1";
        var inventory = new[] { new InventoryItem(vm, "vm1", "microsoft.compute/virtualmachines", "westeurope", "rg1", "s1", new Dictionary<string, string>(), null) };

        var plan = DecommissionPlanner.Plan(inventory, [vm], GuardrailPolicy.Empty,
            locks: [new ArmLock(vm, ArmLockLevel.ReadOnly, "freeze")], roleAssignments: [], backupItems: []);

        Assert.False(plan.Allowed);
        Assert.Contains(plan.Blockers, b => b.Kind == "lock-can-not-delete" && b.Detail.Contains("freeze"));
    }

    // --- workspace ---

    [Fact]
    public async Task LatestCompletedScanCarriesOnlyItsOwnSnapshots()
    {
        var scans = new WorkspaceScans(_workspace);
        foreach (var name in new[] { "older", "newest" })
        {
            var scan = await scans.StartAsync(name, "Tenant");
            await scans.PersistPageAsync(scan.Id,
                [new InventorySnapshotRow($"/subs/s/rg/a/providers/Microsoft.Test/w/{name}", name, "Microsoft.Test/widgets", "westeurope", "rg", "s", null, null)],
                """{"completedQueries":["g"],"rows":1}""");
            await scans.FinishAsync(scan.Id, "Completed", """{"completedQueries":["g"],"rows":1}""", "{}", null);
            await Task.Delay(20);
        }

        var latest = await scans.GetLatestCompletedAsync();

        Assert.Equal("newest", latest!.Scope);
        Assert.Equal("newest", Assert.Single(latest.Snapshots).Name);
    }

    [Fact]
    public async Task PageDeliveredAgainAfterAResumeReplacesItsRows()
    {
        var scans = new WorkspaceScans(_workspace);
        var scan = await scans.StartAsync("tenant", "Tenant");
        static InventorySnapshotRow Row(string name, string location) =>
            new($"/subs/s/rg/a/providers/Microsoft.Test/w/{name}", name, "Microsoft.Test/widgets", location, "rg", "s", null, null);

        await scans.PersistPageAsync(scan.Id, [Row("a", "westeurope"), Row("b", "westeurope")], """{"rows":2}""");
        // Killed before the group was checkpointed as complete; the resume fetches it again,
        // and one resource has moved on in the meantime.
        await scans.PersistPageAsync(scan.Id, [Row("a", "northeurope"), Row("b", "westeurope"), Row("c", "westeurope")], """{"rows":3}""");

        var snapshots = await scans.GetSnapshotsAsync(scan.Id);
        Assert.Equal(new[] { "a", "b", "c" }, snapshots.Select(s => s.Name));
        Assert.Equal("northeurope", snapshots.Single(s => s.Name == "a").Location);
        Assert.Equal(3, (await scans.ListAsync()).Single().ResourceCount);
    }

    [Fact]
    public async Task SameResourceInAnotherScanIsNotTouched()
    {
        var scans = new WorkspaceScans(_workspace);
        var row = new InventorySnapshotRow("/subs/s/rg/a/providers/Microsoft.Test/w/a", "a", "Microsoft.Test/widgets", "westeurope", "rg", "s", null, null);
        var first = await scans.StartAsync("tenant", "Tenant");
        await scans.PersistPageAsync(first.Id, [row], """{"rows":1}""");
        await scans.FinishAsync(first.Id, "Completed", """{"rows":1}""", "{}", null);
        var second = await scans.StartAsync("tenant", "Tenant");

        await scans.PersistPageAsync(second.Id, [row], """{"rows":1}""");

        Assert.Single(await scans.GetSnapshotsAsync(first.Id));
        Assert.Single(await scans.GetSnapshotsAsync(second.Id));
    }

    [Fact]
    public async Task NodePositionsTreatResourceIdsCaseInsensitively()
    {
        var positions = new TopologyPositions(_workspace);
        const string id = "/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1";

        await positions.SaveAsync(id, "frame", 10, 20);
        await positions.SaveAsync(id.ToUpperInvariant(), "frame", 30, 40); // a rescan returned other casing

        var all = await positions.GetAllAsync();
        var saved = Assert.Single(all);
        Assert.Equal((30d, 40d), (saved.Value.X, saved.Value.Y));
        Assert.True(all.ContainsKey(id.ToLowerInvariant()));
    }

    [Fact]
    public async Task NodePositionRowsThatDifferOnlyByCaseNoLongerBreakTheRead()
    {
        const string id = "/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1";
        await using (var db = _workspace.CreateContext())
        {
            db.NodePositions.Add(new NodePositionRecord { NodeId = id, ContainerId = "f", X = 1, Y = 1, SavedAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
            db.NodePositions.Add(new NodePositionRecord { NodeId = id.ToUpperInvariant(), ContainerId = "f", X = 2, Y = 2, SavedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var saved = Assert.Single(await new TopologyPositions(_workspace).GetAllAsync());

        Assert.Equal(2d, saved.Value.X); // the newer row
    }
}

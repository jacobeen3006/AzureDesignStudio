using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.ResourceGraph;
using AzureDesignStudio.Discovery.Scan;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// Scan engine (v2 plan 7.4/7.5): grouped collectors, per-group checkpoints so a cancelled
/// scan resumes without re-querying finished groups, partial failures recorded instead of
/// fatal, pause gate deterministic. Replayed against a scripted HTTP handler, no network.
/// </summary>
public class ScanEngineTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<string, int, HttpResponseMessage> _responder;
        public ConcurrentQueue<string> RequestBodies { get; } = new();

        public FakeHandler(Func<string, int, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Enqueue(body);
            return _responder(body, RequestBodies.Count - 1);
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new AccessToken("fake-token", DateTimeOffset.MaxValue));
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
            => new("fake-token", DateTimeOffset.MaxValue);
    }

    private static string Page(string[] ids)
    {
        var rows = ids.Select(id => new
        {
            id,
            name = id.Split('/')[^1],
            type = "Microsoft.Test/widgets",
            location = "westeurope",
            resourceGroup = "rg1",
            subscriptionId = "s1",
            tags = new { },
            properties = new { size = 3, unknown = new { deep = true } },
        }).ToList();
        return System.Text.Json.JsonSerializer.Serialize(new { data = rows, count = rows.Count, totalRecords = rows.Count, resultTruncated = "False" });
    }

    private static HttpResponseMessage Arg(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static ResourceGraphClient Graph(FakeHandler handler) =>
        new(new HttpClient(handler), new FakeCredential(), null, (_, _) => Task.CompletedTask);

    private sealed class RecordingSink : IScanSink
    {
        public readonly List<(string Key, int Count)> Pages = [];
        public readonly List<string> Statuses = [];
        public readonly TaskCompletionSource<string> FirstPage = new();
        public ScanCheckpoint? LastCheckpoint;
        public string? StartScope;
        public bool? Resumed;
        public Func<int, Task>? onPage;

        public Task ScanStartedAsync(ScanRequest request, ScanCheckpoint checkpoint, bool isResume, CancellationToken ct)
        {
            StartScope = request.Scope;
            Resumed = isResume;
            return Task.CompletedTask;
        }

        public async Task PagePersistedAsync(string queryKey, IReadOnlyList<InventoryItem> page, ScanCheckpoint checkpoint, CancellationToken ct)
        {
            Pages.Add((queryKey, page.Count));
            LastCheckpoint = checkpoint;
            if (onPage is { } cb) await cb(Pages.Count);
            FirstPage.TrySetResult(queryKey);
        }

        public Task ScanFinishedAsync(string status, ScanCheckpoint checkpoint, CancellationToken ct)
        {
            Statuses.Add(status);
            LastCheckpoint = checkpoint;
            return Task.CompletedTask;
        }
    }

    private static ScanRequest Request(params string[] subs) => new("tenant", "Tenant", subs);

    [Fact]
    public async Task RunAsync_CompletesAllGroups_CountsAndHashes()
    {
        var handler = new FakeHandler((_, _) => Arg(Page(["/subs/s1/rg/a/providers/Microsoft.Test/w/one", "/subs/s1/rg/a/providers/Microsoft.Test/w/two"])));
        var sink = new RecordingSink();
        var engine = new ScanEngine(Graph(handler), groupSize: 2);

        var checkpoint = await engine.RunAsync(Request("s1", "s2", "s3"), sink);

        Assert.Equal(["Completed"], sink.Statuses);
        Assert.Equal(2, sink.Pages.Count);
        Assert.Equal(4, checkpoint.Rows);
        Assert.Equal(2, checkpoint.CompletedQueries.Count);
        Assert.Empty(checkpoint.FailedQueries);
        Assert.NotNull(checkpoint.ContentHash);
        Assert.Equal("tenant", sink.StartScope);
        Assert.False(sink.Resumed);
        // grouped queries: s1+s2 together, s3 alone
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task RunAsync_GroupFails_FailureRecordedScanStillCompletes()
    {
        var handler = new FakeHandler((body, _) => body.Contains("bad")
            ? Arg("""{"error":{"code":"BadRequest","message":"nope"}}""", HttpStatusCode.BadRequest)
            : Arg(Page(["/subs/good/rg/a/providers/Microsoft.Test/w/one"])));
        var sink = new RecordingSink();
        var engine = new ScanEngine(Graph(handler), groupSize: 1);

        var checkpoint = await engine.RunAsync(Request("good", "bad"), sink);

        Assert.Equal(["Completed"], sink.Statuses);
        Assert.Single(checkpoint.CompletedQueries);
        Assert.Single(checkpoint.FailedQueries);
        Assert.Contains("BadRequest", checkpoint.FailedQueries.Values.Single());
        Assert.Equal(1, checkpoint.Rows);
        Assert.Single(sink.Pages);
    }

    [Fact]
    public async Task RunAsync_Cancelled_ResumedRunSkipsFinishedGroups()
    {
        var handler = new FakeHandler((body, _) => body.Contains("b")
            ? Arg(Page(["/subs/b/rg/a/providers/Microsoft.Test/w/one", "/subs/b/rg/a/providers/Microsoft.Test/w/two"]))
            : Arg(Page(["/subs/a/rg/a/providers/Microsoft.Test/w/one"])));
        var engine = new ScanEngine(Graph(handler), groupSize: 1, maxParallel: 1);

        var cts = new CancellationTokenSource();
        var firstSink = new RecordingSink { onPage = _ => { cts.Cancel(); return Task.CompletedTask; } };
        var first = await engine.RunAsync(Request("a", "b"), firstSink, control: null, ct: cts.Token);

        Assert.Equal(["Canceled"], firstSink.Statuses);
        Assert.Single(firstSink.Pages);
        Assert.Single(first.CompletedQueries); // one group finished and is durable
        Assert.True(first.Rows >= 1);

        // resume: finished group must never be re-queried
        var resumedHandler = new FakeHandler((_, _) => Arg(Page(["/subs/b/rg/a/providers/Microsoft.Test/w/one", "/subs/b/rg/a/providers/Microsoft.Test/w/two"])));
        var resumeSink = new RecordingSink();
        var resumed = await engine.RunAsync(Request("a", "b"), resumeSink, resume: ScanCheckpoint.FromJson(first.ToJson()));

        Assert.DoesNotContain(resumedHandler.RequestBodies, b => b.Contains("'a'"));
        Assert.True(resumeSink.Resumed);
        Assert.Equal(2, resumed.CompletedQueries.Count);
    }

    [Fact]
    public async Task RunAsync_PauseBlocksNextGroup_UntilResume()
    {
        var handler = new FakeHandler((_, _) => Arg(Page(["/subs/x/rg/a/providers/Microsoft.Test/w/one"])));
        var sink = new RecordingSink();
        var control = new ScanControl();
        // Pause inside the first page callback: the first worker still holds its slot,
        sink.onPage = n => { if (n == 1) control.Pause(); return Task.CompletedTask; };
        var engine = new ScanEngine(Graph(handler), groupSize: 1, maxParallel: 1);

        var run = engine.RunAsync(Request("a", "b"), sink, control: control);
        await sink.FirstPage.Task;
        Assert.True(control.Paused);
        await Task.Delay(150);
        Assert.Single(handler.RequestBodies); // second group stalled by the gate

        control.Resume();
        var checkpoint = await run;
        Assert.Equal(2, sink.Pages.Count);
        Assert.Equal(["Completed"], sink.Statuses);
        Assert.Equal(2, checkpoint.CompletedQueries.Count);
    }

    [Fact]
    public async Task Checkpoint_JsonRoundTrips()
    {
        var handler = new FakeHandler((_, _) => Arg(Page(["/subs/s/rg/a/providers/Microsoft.Test/w/one"])));
        var checkpoint = await new ScanEngine(Graph(handler), groupSize: 1)
            .RunAsync(Request("s"), new RecordingSink());

        var restored = ScanCheckpoint.FromJson(checkpoint.ToJson())!;
        Assert.Equal(checkpoint.CompletedQueries, restored.CompletedQueries);
        Assert.Equal(checkpoint.Rows, restored.Rows);
        Assert.Equal(checkpoint.ContentHash, restored.ContentHash);
        Assert.Equal(checkpoint.ScanId, restored.ScanId);
    }

    [Fact]
    public async Task ContentHash_StableForIdenticalInventory()
    {
        var json = Page(["/subs/s/rg/a/providers/Microsoft.Test/w/one", "/subs/s/rg/b/providers/Microsoft.Test/w/two"]);
        var h1 = await new ScanEngine(Graph(new FakeHandler((_, _) => Arg(json))), groupSize: 1)
            .RunAsync(Request("s"), new RecordingSink());
        var h2 = await new ScanEngine(Graph(new FakeHandler((_, _) => Arg(json))), groupSize: 1)
            .RunAsync(Request("s"), new RecordingSink());
        Assert.Equal(h1.ContentHash, h2.ContentHash);
    }

    [Fact]
    public void Normalizer_KeepsUnknownTypeAndPropertiesVerbatim()
    {
        var row = System.Text.Json.JsonDocument.Parse("""
            {"id":"/subs/s/rg/r/providers/Microsoft.Novel/widget/n","name":"n","type":"Microsoft.Novel/widget","location":"westus","resourceGroup":"r","subscriptionId":"s","tags":{"Owner":"alice","Do-Not-Touch":"true"},"properties":{"brandNew":{"deep":"value"}},"futureColumn":"keep-me"}
            """).RootElement;

        var item = InventoryNormalizer.FromArgRow(row);

        Assert.Equal("Microsoft.Novel", item.ProviderNamespace);
        Assert.Equal("widget", item.ResourceTypeSegment);
        Assert.Equal("/subs/s/rg/r/providers/Microsoft.Novel/widget", item.ParentId);
        Assert.Equal("alice", item.Owner);
        Assert.True(item.HasCostEscalationSignal);
        Assert.Equal("value", item.Raw!.Value.GetProperty("brandNew").GetProperty("deep").GetString());
    }

    private static InventoryItem Item(string id, string type, string location, params (string, string)[] tags) =>
        new(id, id.Split('/')[^1], type, location, "rg", "sub1",
            tags.ToDictionary(t => t.Item1, t => t.Item2, StringComparer.OrdinalIgnoreCase), null);

    [Fact]
    public void Facets_GroupOwnersTagsAndCountsUntagged()
    {
        var items = new[]
        {
            Item("/subs/sub1/rg/net/providers/Microsoft.Network/virtualNetworks/v1", "Microsoft.Network/virtualNetworks", "westeurope", ("owner", "alice")),
            Item("/subs/sub1/rg/net/providers/Microsoft.Network/networkInterfaces/n1", "Microsoft.Network/networkInterfaces", "westeurope", ("Owner", "bob")),
            Item("/subs/sub1/rg/db/providers/Microsoft.Novel/widget/w1", "Microsoft.Novel/widget", "", ("env", "prod"), ("owner", "alice")),
            Item("/subs/sub1/rg/db/providers/Microsoft.Novel/widget/w2", "Microsoft.Novel/widget", "westus2"),
        };

        var facets = InventoryFacets.From(items);

        Assert.Equal(4, facets.Total);
        Assert.Equal(1, facets.Untagged);
        Assert.Equal(new[] { "alice", "(unowned)", "bob" }, facets.Owners.Select(f => f.Value).ToArray());
        Assert.Equal(2, facets.Owners[0].Count); // alice owns two
        Assert.Equal(1, facets.Owners[1].Count); // the untagged gap is a visible facet
        Assert.Equal(["Microsoft.Network", "Microsoft.Novel"], facets.Providers.Select(f => f.Value).OrderBy(v => v).ToArray());
        Assert.Equal("global", facets.Locations.Single(f => f.Count == 1 && f.Value != "westeurope" && f.Value != "westus2").Value);
        Assert.True(facets.TagKeys.ContainsKey("env"));
        Assert.Single(facets.TagKeys["env"]);
    }

    [Fact]
    public void CloudProfile_EndpointsAndArgSupportPerCloud()
    {
        Assert.True(AzureDesignStudio.Discovery.Arm.CloudProfile.AzureCloud.ArgSupported);
        Assert.True(AzureDesignStudio.Discovery.Arm.CloudProfile.AzureUSGovernment.ArgSupported);
        Assert.False(AzureDesignStudio.Discovery.Arm.CloudProfile.AzureChinaCloud.ArgSupported);
        Assert.All(AzureDesignStudio.Discovery.Arm.CloudProfile.All, p =>
        {
            Assert.Contains(p.Arm.ManagementEndpoint, p.GraphEndpoint);
            Assert.EndsWith("/.default", p.GraphScope);
            Assert.Equal(p.GraphScope, p.GraphOptions().Scope);
        });
        Assert.Equal("AzureUSGovernment", AzureDesignStudio.Discovery.Arm.CloudProfile.ForName("azureusgovernment").Name);
        Assert.Equal("AzureCloud", AzureDesignStudio.Discovery.Arm.CloudProfile.ForName(null).Name);
        Assert.Equal("AzureCloud", AzureDesignStudio.Discovery.Arm.CloudProfile.ForName("bogus").Name);
    }
}

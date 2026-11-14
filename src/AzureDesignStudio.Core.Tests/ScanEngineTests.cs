using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using AzureDesignStudio.Discovery.Governance;
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
    public async Task RunAsync_GroupFails_FailureRecordedScanFinishesAsPartial()
    {
        var handler = new FakeHandler((body, _) => body.Contains("bad")
            ? Arg("""{"error":{"code":"BadRequest","message":"nope"}}""", HttpStatusCode.BadRequest)
            : Arg(Page(["/subs/good/rg/a/providers/Microsoft.Test/w/one"])));
        var sink = new RecordingSink();
        var engine = new ScanEngine(Graph(handler), groupSize: 1);

        var checkpoint = await engine.RunAsync(Request("good", "bad"), sink);

        Assert.Equal(["Partial"], sink.Statuses); // never the baseline a later scan is compared with
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
        var resumeEngine = new ScanEngine(Graph(resumedHandler), groupSize: 1, maxParallel: 1);
        var resumed = await resumeEngine.RunAsync(Request("a", "b"), resumeSink, resume: ScanCheckpoint.FromJson(first.ToJson()));

        Assert.Single(resumedHandler.RequestBodies);
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
        sink.onPage = async n => { if (n == 1) await control.PauseAsync(); };
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

    private sealed class ThrowingHandler(Func<string, Exception?> failure, Func<string, HttpResponseMessage> ok) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (failure(body) is { } e) throw e;
            return ok(body);
        }
    }

    // A socket error or an HttpClient timeout is one group's failure. It used to escape the
    // run before ScanFinishedAsync, leaving the scan recorded as Running with no error.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_TransportFailureOrTimeout_IsAGroupFailureAndTheScanStillFinishes(bool timeout)
    {
        Exception failure = timeout
            ? new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")
            : new HttpRequestException("No such host is known.");
        var handler = new ThrowingHandler(
            body => body.Contains("bad") ? failure : null,
            _ => Arg(Page(["/subs/good/rg/a/providers/Microsoft.Test/w/one"])));
        var sink = new RecordingSink();
        var engine = new ScanEngine(new ResourceGraphClient(new HttpClient(handler), new FakeCredential(), null, (_, _) => Task.CompletedTask), groupSize: 1);

        var checkpoint = await engine.RunAsync(Request("good", "bad"), sink);

        Assert.Equal(["Partial"], sink.Statuses);
        Assert.Single(checkpoint.CompletedQueries);
        Assert.Contains(failure.GetType().Name, Assert.Single(checkpoint.FailedQueries).Value);
    }

    [Fact]
    public async Task RunAsync_EveryGroupSucceeding_IsCompleted()
    {
        var handler = new FakeHandler((_, _) => Arg(Page(["/subs/s/rg/a/providers/Microsoft.Test/w/one"])));
        var sink = new RecordingSink();
        var engine = new ScanEngine(Graph(handler), groupSize: 1);

        var checkpoint = await engine.RunAsync(Request("a", "b"), sink);

        Assert.Equal(["Completed"], sink.Statuses);
        Assert.Empty(checkpoint.FailedQueries);
        Assert.False(checkpoint.AnyTruncated);
    }

    [Fact]
    public async Task RunAsync_PageThatCouldNotBeStored_IsNotMarkedComplete()
    {
        var handler = new FakeHandler((_, _) => Arg(Page(["/subs/s/rg/a/providers/Microsoft.Test/w/one"])));
        var sink = new RecordingSink { onPage = n => n == 1 ? throw new InvalidOperationException("database is locked") : Task.CompletedTask };
        var engine = new ScanEngine(Graph(handler), groupSize: 1, maxParallel: 1);

        var checkpoint = await engine.RunAsync(Request("a", "b"), sink);

        // The group whose page was lost must be queried again by the next run.
        Assert.Single(checkpoint.CompletedQueries);
        Assert.Equal(1, checkpoint.Rows);
        Assert.Contains("database is locked", Assert.Single(checkpoint.FailedQueries).Value);
        Assert.DoesNotContain(Assert.Single(checkpoint.FailedQueries).Key, checkpoint.CompletedQueries);
    }

    private sealed class SnapshotSink : IScanSink
    {
        public readonly List<int> CompletedAtPersist = [];
        public CancellationToken FinishToken;
        public string? FinishStatus;
        public Func<Task>? OnPage;

        public Task ScanStartedAsync(ScanRequest request, ScanCheckpoint checkpoint, bool isResume, CancellationToken ct) => Task.CompletedTask;

        public async Task PagePersistedAsync(string queryKey, IReadOnlyList<InventoryItem> page, ScanCheckpoint checkpoint, CancellationToken ct)
        {
            // What a real sink does: serialise the checkpoint and store it with the page.
            var stored = ScanCheckpoint.FromJson(checkpoint.ToJson())!;
            await Task.Yield();
            lock (CompletedAtPersist) CompletedAtPersist.Add(stored.CompletedQueries.Count);
            Assert.Contains(queryKey, stored.CompletedQueries);
            if (OnPage is { } cb) await cb();
        }

        public Task ScanFinishedAsync(string status, ScanCheckpoint checkpoint, CancellationToken ct)
        {
            FinishStatus = status;
            FinishToken = ct;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    // Each stored checkpoint must hold exactly the groups stored so far - never one whose
    // rows are still in flight, never an older snapshot landing after a newer one.
    [Fact]
    public async Task RunAsync_ParallelGroups_StoreCheckpointsInOrder()
    {
        var handler = new FakeHandler((_, _) => Arg(Page(["/subs/s/rg/a/providers/Microsoft.Test/w/one"])));
        var sink = new SnapshotSink();
        var engine = new ScanEngine(Graph(handler), groupSize: 1, maxParallel: 4);
        var subs = Enumerable.Range(0, 24).Select(i => $"sub{i}").ToArray();

        var checkpoint = await engine.RunAsync(Request(subs), sink);

        Assert.Equal(24, checkpoint.CompletedQueries.Count);
        Assert.Equal(Enumerable.Range(1, 24), sink.CompletedAtPersist);
    }

    [Fact]
    public async Task RunAsync_Cancelled_StoresTheTerminalStatusWithALiveToken()
    {
        var handler = new FakeHandler((_, _) => Arg(Page(["/subs/s/rg/a/providers/Microsoft.Test/w/one"])));
        var cts = new CancellationTokenSource();
        var sink = new SnapshotSink { OnPage = () => { cts.Cancel(); return Task.CompletedTask; } };
        var engine = new ScanEngine(Graph(handler), groupSize: 1, maxParallel: 1);

        await engine.RunAsync(Request("a", "b"), sink, ct: cts.Token);

        Assert.Equal("Canceled", sink.FinishStatus);
        Assert.False(sink.FinishToken.IsCancellationRequested);
    }

    // ---------- governance sources (Wave B3) ----------
    // Each subscription group gets one extra worker per requested source. A source that fails
    // or comes back truncated is recorded in FailedSourceGroups only: the scan's status, rows,
    // content hash and inventory failures never see it.

    private const string SubA = "00000000-0000-0000-0000-0000000000a1";
    private const string SubB = "00000000-0000-0000-0000-0000000000b2";

    private static string Vm(string sub) => $"/subscriptions/{sub}/resourceGroups/rg-app/providers/Microsoft.Compute/virtualMachines/vm1";

    /// <summary>The Resource Graph query a recorded request body carries.</summary>
    private static string QueryOf(string body)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("query").GetString()!;
    }

    private static string KindOf(string body)
    {
        var query = QueryOf(body);
        return query.StartsWith("advisorresources ", StringComparison.Ordinal) ? "advisor"
            : query.StartsWith("policyresources ", StringComparison.Ordinal) ? "policy"
            : query.Contains("'microsoft.security/assessments'", StringComparison.Ordinal) ? "defender-assessments"
            : query.Contains("'microsoft.security/pricings'", StringComparison.Ordinal) ? "defender-plans"
            : query.StartsWith("Resources ", StringComparison.Ordinal) ? "inventory"
            : "unknown";
    }

    /// <summary>The single subscription a groupSize-1 query filters on.</summary>
    private static string SubOf(string body) =>
        System.Text.RegularExpressions.Regex.Match(QueryOf(body), @"subscriptionId in \('([^']*)'\)").Groups[1].Value;

    private static string Rows(bool truncated, params object[] rows) =>
        System.Text.Json.JsonSerializer.Serialize(new { data = rows, count = rows.Length, totalRecords = rows.Length, resultTruncated = truncated ? "true" : "false" });

    private static object AdvisorRow(string sub, string name, string status = "") => new
    {
        id = $"{Vm(sub)}/providers/Microsoft.Advisor/recommendations/{name}",
        subscriptionId = sub,
        resourceId = Vm(sub),
        source = Vm(sub),
        category = "HighAvailability",
        impact = "High",
        problem = $"Enable backup ({name})",
        solution = "Protect the virtual machine with Azure Backup",
        status,
        suppressions = "",
    };

    private static object PolicyRow(string sub, string stateName) => new
    {
        id = $"{Vm(sub)}/providers/Microsoft.PolicyInsights/policyStates/{stateName}",
        subscriptionId = sub,
        resourceId = Vm(sub),
        assignmentId = $"/subscriptions/{sub}/providers/Microsoft.Authorization/policyAssignments/require-tags",
        assignmentName = "require-tags",
        definitionId = "/providers/Microsoft.Authorization/policyDefinitions/871b6d14-10aa-478d-b590-94f262ecfa99",
        referenceId = "",
    };

    private static object AssessmentRow(string sub) => new
    {
        id = $"{Vm(sub)}/providers/Microsoft.Security/assessments/d57a4221-a804-52ca-3dea-768284f06bb7",
        subscriptionId = sub,
        targetId = Vm(sub),
        name = "Machines should have vulnerability findings resolved",
        severity = "High",
    };

    private static object PlanRow(string sub, string plan, string tier) => new
    {
        id = $"/subscriptions/{sub}/providers/Microsoft.Security/pricings/{plan}",
        subscriptionId = sub,
        plan,
        tier,
    };

    /// <summary>
    /// Answers every query for the subscription it filters on: one inventory row; Advisor one
    /// open and one dismissed recommendation; Policy the same non-compliance twice (two policy
    /// states); Defender one unhealthy assessment and two plans. <paramref name="answer"/> may
    /// replace the answer for a (kind, subscription).
    /// </summary>
    private static FakeHandler GovernanceHandler(Func<string, string, HttpResponseMessage?>? answer = null) =>
        new((body, _) =>
        {
            var kind = KindOf(body);
            var sub = SubOf(body);
            if (answer?.Invoke(kind, sub) is { } replaced)
                return replaced;
            return Arg(kind switch
            {
                "inventory" => Page([$"/subscriptions/{sub}/resourceGroups/rg-app/providers/Microsoft.Test/widgets/w1"]),
                "advisor" => Rows(false, AdvisorRow(sub, "rec1"), AdvisorRow(sub, "rec2", status: "Dismissed")),
                "policy" => Rows(false, PolicyRow(sub, "state1"), PolicyRow(sub, "state2")),
                "defender-assessments" => Rows(false, AssessmentRow(sub)),
                "defender-plans" => Rows(false, PlanRow(sub, "VirtualMachines", "Standard"), PlanRow(sub, "CloudPosture", "free")),
                _ => throw new InvalidOperationException("unexpected query: " + QueryOf(body)),
            });
        });

    private static HttpResponseMessage Forbidden() =>
        Arg("""{"error":{"code":"AuthorizationFailed","message":"The client does not have authorization to perform action 'Microsoft.ResourceGraph/resources/read'."}}""", HttpStatusCode.Forbidden);

    private static ScanRequest SourcesRequest(IReadOnlyList<string>? sources, params string[] subs) => new("tenant", "SubscriptionSet", subs, sources);

    private sealed record StoredSourcePage(string Source, string Key, IReadOnlyList<GovernanceFinding> Findings, ScanCheckpoint Stored);

    /// <summary>A sink that stores what a real one would: each page with a serialized copy
    /// of the checkpoint handed with it.</summary>
    private sealed class GovernanceSink : IScanSink
    {
        public readonly List<string> InventoryPages = [];
        public readonly List<StoredSourcePage> SourcePages = [];
        /// <summary>The checkpoint JSON stored with the last inventory page.</summary>
        public string? InventoryCheckpointJson;
        public ScanCheckpoint? Started;
        public string? Status;
        public Func<int, Task>? OnSourcePage;

        public Task ScanStartedAsync(ScanRequest request, ScanCheckpoint checkpoint, bool isResume, CancellationToken ct)
        {
            Started = ScanCheckpoint.FromJson(checkpoint.ToJson());
            return Task.CompletedTask;
        }

        public Task PagePersistedAsync(string queryKey, IReadOnlyList<InventoryItem> page, ScanCheckpoint checkpoint, CancellationToken ct)
        {
            lock (InventoryPages)
            {
                InventoryPages.Add(queryKey);
                InventoryCheckpointJson = checkpoint.ToJson();
            }
            return Task.CompletedTask;
        }

        public async Task SourcePagePersistedAsync(string source, string queryKey, IReadOnlyList<GovernanceFinding> findings, ScanCheckpoint checkpoint, CancellationToken ct)
        {
            var stored = ScanCheckpoint.FromJson(checkpoint.ToJson())!;
            int count;
            lock (SourcePages)
            {
                SourcePages.Add(new(source, queryKey, findings, stored));
                count = SourcePages.Count;
            }
            if (OnSourcePage is { } cb) await cb(count);
        }

        public Task ScanFinishedAsync(string status, ScanCheckpoint checkpoint, CancellationToken ct)
        {
            Status = status;
            return Task.CompletedTask;
        }
    }

    private static string InventoryKey(ScanCheckpoint checkpoint) => Assert.Single(checkpoint.CompletedQueries, k => !k.Contains(':'));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sources_NotRequested_NoExtraQueries(bool emptyList)
    {
        var handler = GovernanceHandler();
        var sink = new GovernanceSink();
        var engine = new ScanEngine(Graph(handler), groupSize: 1);

        var checkpoint = await engine.RunAsync(SourcesRequest(emptyList ? [] : null, SubA, SubB), sink);

        Assert.Equal("Completed", sink.Status);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.All(handler.RequestBodies, b => Assert.Equal("inventory", KindOf(b)));
        Assert.Empty(sink.SourcePages);
        Assert.Equal(2, checkpoint.CompletedQueries.Count);
        Assert.DoesNotContain(checkpoint.CompletedQueries, k => k.Contains(':'));
        Assert.Empty(checkpoint.RequestedSources);
        Assert.Empty(checkpoint.SourceRows);
        Assert.Empty(checkpoint.FailedSourceGroups);
        Assert.Empty(checkpoint.DefenderPlans);
    }

    [Fact]
    public async Task Sources_Requested_RowsReachTheSink()
    {
        var handler = GovernanceHandler();
        var sink = new GovernanceSink();
        var engine = new ScanEngine(Graph(handler), groupSize: 1);

        var checkpoint = await engine.RunAsync(SourcesRequest(["defender", "advisor", "policy"], SubA), sink);

        Assert.Equal("Completed", sink.Status);
        // RequestedSources is recorded before the scan starts, in canonical order.
        Assert.Equal(["advisor", "policy", "defender"], sink.Started!.RequestedSources);
        Assert.Equal(["advisor", "policy", "defender"], checkpoint.RequestedSources);
        // One inventory query, one per source, and Defender's two in its one worker.
        Assert.Equal(["advisor", "defender-assessments", "defender-plans", "inventory", "policy"],
            handler.RequestBodies.Select(KindOf).Order(StringComparer.Ordinal).ToArray());
        Assert.All(handler.RequestBodies, b => Assert.Equal(SubA, SubOf(b)));

        var group = InventoryKey(checkpoint);
        Assert.Equal(["advisor", "defender", "policy"], sink.SourcePages.Select(p => p.Source).Order(StringComparer.Ordinal).ToArray());
        Assert.All(sink.SourcePages, p => Assert.Equal($"{p.Source}:{group}", p.Key));

        // Normalized (the dismissed recommendation is dropped) and deduped (two policy states,
        // one finding).
        var advisor = Assert.Single(sink.SourcePages.Single(p => p.Source == "advisor").Findings);
        Assert.Equal(GovernanceSource.Advisor, advisor.Source);
        Assert.Equal("Enable backup (rec1)", advisor.Title);
        var policy = Assert.Single(sink.SourcePages.Single(p => p.Source == "policy").Findings);
        Assert.Equal("require-tags", policy.Title);
        var defender = Assert.Single(sink.SourcePages.Single(p => p.Source == "defender").Findings);
        Assert.Equal(Vm(SubA), defender.ResourceId);
        Assert.Equal(GovernanceSeverity.High, defender.Severity);

        Assert.Equal(new Dictionary<string, int> { ["advisor"] = 1, ["policy"] = 1, ["defender"] = 1 }, checkpoint.SourceRows);
        Assert.Equal(new[] { new DefenderPlan(SubA, "CloudPosture", "Free"), new DefenderPlan(SubA, "VirtualMachines", "Standard") },
            checkpoint.DefenderPlans);
        Assert.Contains($"advisor:{group}", checkpoint.CompletedQueries);
        Assert.Contains($"policy:{group}", checkpoint.CompletedQueries);
        Assert.Contains($"defender:{group}", checkpoint.CompletedQueries);
        Assert.Empty(checkpoint.FailedSourceGroups);
        Assert.Single(sink.InventoryPages);
        Assert.Equal(1, checkpoint.Rows); // findings are not inventory rows
    }

    [Fact]
    public async Task SourcePage_CheckpointNamesTheSourceGroup()
    {
        var sink = new GovernanceSink();
        var engine = new ScanEngine(Graph(GovernanceHandler()), groupSize: 1, maxParallel: 1);

        await engine.RunAsync(SourcesRequest(["advisor", "policy", "defender"], SubA, SubB), sink);

        Assert.Equal(6, sink.SourcePages.Count);
        foreach (var page in sink.SourcePages)
        {
            // What the sink stores with the findings already says they are stored: a kill right
            // after this write resumes without querying the group again.
            Assert.Contains(page.Key, page.Stored.CompletedQueries);
            Assert.DoesNotContain(page.Key, page.Stored.FailedSourceGroups.Keys);
            Assert.True(page.Stored.SourceRows[page.Source] >= page.Findings.Count);
            Assert.Equal(["advisor", "policy", "defender"], page.Stored.RequestedSources);
        }

        // The plans of a Defender group are in the checkpoint stored with its assessments.
        foreach (var page in sink.SourcePages.Where(p => p.Source == "defender"))
        {
            var sub = page.Findings.Single().SubscriptionId;
            Assert.Contains(page.Stored.DefenderPlans, p => p.SubscriptionId == sub && p.Plan == "VirtualMachines");
        }

        // SourceRows counts each source over its stored groups.
        var last = sink.SourcePages[^1].Stored;
        Assert.Equal(new Dictionary<string, int> { ["advisor"] = 2, ["policy"] = 2, ["defender"] = 2 }, last.SourceRows);
    }

    [Fact]
    public async Task SourceSinkThrows_RollsBack()
    {
        var sink = new GovernanceSink { OnSourcePage = n => n == 2 ? throw new InvalidOperationException("database is locked") : Task.CompletedTask };
        var engine = new ScanEngine(Graph(GovernanceHandler()), groupSize: 1, maxParallel: 1);

        var checkpoint = await engine.RunAsync(SourcesRequest(["defender"], SubA, SubB), sink);

        Assert.Equal(2, sink.SourcePages.Count);
        var storedSub = sink.SourcePages[0].Findings.Single().SubscriptionId;
        var lost = sink.SourcePages[1];

        // The page that could not be stored is not complete, its rows and plans are taken back,
        // and it is this source group's failure - not the scan's.
        Assert.DoesNotContain(lost.Key, checkpoint.CompletedQueries);
        Assert.Contains(sink.SourcePages[0].Key, checkpoint.CompletedQueries);
        Assert.Equal(1, checkpoint.SourceRows["defender"]);
        Assert.Equal(2, checkpoint.DefenderPlans.Count);
        Assert.All(checkpoint.DefenderPlans, p => Assert.Equal(storedSub, p.SubscriptionId));
        var failure = Assert.Single(checkpoint.FailedSourceGroups);
        Assert.Equal(lost.Key, failure.Key);
        Assert.Contains("database is locked", failure.Value);
        Assert.Equal("Completed", sink.Status);
        Assert.Empty(checkpoint.FailedQueries);
        Assert.Equal(2, checkpoint.Rows);
    }

    [Fact]
    public async Task SourceSinkThrows_OnTheOnlyGroup_LeavesNoSourceRows()
    {
        var sink = new GovernanceSink { OnSourcePage = _ => throw new InvalidOperationException("disk full") };
        var engine = new ScanEngine(Graph(GovernanceHandler()), groupSize: 1);

        var checkpoint = await engine.RunAsync(SourcesRequest(["defender"], SubA), sink);

        Assert.DoesNotContain(checkpoint.CompletedQueries, k => k.StartsWith("defender:", StringComparison.Ordinal));
        Assert.False(checkpoint.SourceRows.ContainsKey("defender"));
        Assert.Empty(checkpoint.DefenderPlans);
        Assert.Contains("disk full", Assert.Single(checkpoint.FailedSourceGroups).Value);
        Assert.Equal("Completed", sink.Status);
    }

    [Fact]
    public async Task SourceFailure_MarksOnlyThatSourceUnavailable_ScanCompleted()
    {
        var handler = GovernanceHandler((kind, _) => kind == "policy" ? Forbidden() : null);
        var sink = new GovernanceSink();
        var engine = new ScanEngine(Graph(handler), groupSize: 1);

        var checkpoint = await engine.RunAsync(SourcesRequest(GovernanceSourcesAll, SubA), sink);

        var group = InventoryKey(checkpoint);
        Assert.Equal("Completed", sink.Status);
        Assert.Empty(checkpoint.FailedQueries);
        Assert.False(checkpoint.AnyTruncated);
        var failure = Assert.Single(checkpoint.FailedSourceGroups);
        Assert.Equal($"policy:{group}", failure.Key);
        Assert.Contains("AuthorizationFailed", failure.Value);
        Assert.DoesNotContain($"policy:{group}", checkpoint.CompletedQueries);
        Assert.Contains($"advisor:{group}", checkpoint.CompletedQueries);
        Assert.Contains($"defender:{group}", checkpoint.CompletedQueries);
        Assert.DoesNotContain(sink.SourcePages, p => p.Source == "policy");
        Assert.False(checkpoint.SourceRows.ContainsKey("policy"));

        var manifest = ScanManifest.Build("Completed", checkpoint, 1);
        Assert.Equal(SourceState.Unavailable, manifest.Sources!["policy"].State);
        Assert.Contains("AuthorizationFailed", manifest.Sources["policy"].Detail);
        Assert.Equal(new SourceState(SourceState.Collected, 1, null), manifest.Sources["advisor"]);
        Assert.Equal(new SourceState(SourceState.Collected, 1, null), manifest.Sources["defender"]);
    }

    [Fact]
    public async Task SourceTruncation_MarksUnavailable()
    {
        var handler = GovernanceHandler((kind, sub) => kind == "advisor" ? Arg(Rows(true, AdvisorRow(sub, "rec1"))) : null);
        var sink = new GovernanceSink();
        var engine = new ScanEngine(Graph(handler), groupSize: 1);

        var checkpoint = await engine.RunAsync(SourcesRequest(["advisor", "policy"], SubA), sink);

        var group = InventoryKey(checkpoint);
        Assert.Equal("Completed", sink.Status);
        Assert.False(checkpoint.AnyTruncated); // the inventory was complete
        Assert.Empty(checkpoint.FailedQueries);
        var failure = Assert.Single(checkpoint.FailedSourceGroups);
        Assert.Equal($"advisor:{group}", failure.Key);
        Assert.StartsWith("Truncated", failure.Value);
        // A cut-short listing is not stored as if it were the whole one.
        Assert.DoesNotContain($"advisor:{group}", checkpoint.CompletedQueries);
        Assert.DoesNotContain(sink.SourcePages, p => p.Source == "advisor");
        Assert.False(checkpoint.SourceRows.ContainsKey("advisor"));
        Assert.Equal(SourceState.Unavailable, ScanManifest.Build("Completed", checkpoint, 1).Sources!["advisor"].State);
    }

    [Fact]
    public async Task Resume_SkipsCompletedSourceGroups()
    {
        var cts = new CancellationTokenSource();
        var firstSink = new GovernanceSink { OnSourcePage = _ => { cts.Cancel(); return Task.CompletedTask; } };
        var first = await new ScanEngine(Graph(GovernanceHandler()), groupSize: 1, maxParallel: 1)
            .RunAsync(SourcesRequest(["advisor", "policy"], SubA), firstSink, ct: cts.Token);

        Assert.Equal("Canceled", firstSink.Status);
        var stored = Assert.Single(firstSink.SourcePages); // the one source group that finished
        Assert.Contains(stored.Key, first.CompletedQueries);

        var saved = ScanCheckpoint.FromJson(first.ToJson())!;
        var pending = new[] { "inventory", "advisor", "policy" }
            .Where(kind => !saved.CompletedQueries.Any(k => KindOfKey(k) == kind))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(pending);

        var handler = GovernanceHandler();
        var sink = new GovernanceSink();
        var resumed = await new ScanEngine(Graph(handler), groupSize: 1, maxParallel: 1)
            .RunAsync(SourcesRequest(["advisor", "policy"], SubA), sink, resume: saved);

        // Exactly the groups the interrupted run did not finish are queried.
        Assert.Equal(pending, handler.RequestBodies.Select(KindOf).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(handler.RequestBodies, b => KindOf(b) == stored.Source);
        Assert.Equal("Completed", sink.Status);
        Assert.Equal(3, resumed.CompletedQueries.Count);
        Assert.Equal(new Dictionary<string, int> { ["advisor"] = 1, ["policy"] = 1 }, resumed.SourceRows);
        Assert.All(ScanManifest.Build("Completed", resumed, 1).Sources!.Where(s => s.Key != "defender"),
            s => Assert.Equal(SourceState.Collected, s.Value.State));
    }

    private static string KindOfKey(string key) => key.Contains(':') ? key[..key.IndexOf(':')] : "inventory";

    [Fact]
    public async Task Resume_KeepsTheSourcesTheScanStartedWith()
    {
        var first = await new ScanEngine(Graph(GovernanceHandler((kind, _) => kind == "advisor" ? Forbidden() : null)), groupSize: 1)
            .RunAsync(SourcesRequest(["advisor"], SubA), new GovernanceSink());
        Assert.Single(first.FailedSourceGroups);

        var handler = GovernanceHandler();
        var resumed = await new ScanEngine(Graph(handler), groupSize: 1)
            .RunAsync(SourcesRequest(GovernanceSourcesAll, SubA), new GovernanceSink(), resume: ScanCheckpoint.FromJson(first.ToJson()));

        // The interrupted scan asked for Advisor only; resuming finishes that scan, it does not
        // widen it.
        Assert.Equal(["advisor"], resumed.RequestedSources);
        Assert.Equal(["advisor"], handler.RequestBodies.Select(KindOf).ToArray());
    }

    [Fact]
    public async Task Resume_RetrySucceeds_ClearsFailure()
    {
        var firstSink = new GovernanceSink();
        var first = await new ScanEngine(Graph(GovernanceHandler((kind, sub) => kind == "policy" && sub == SubB ? Forbidden() : null)), groupSize: 1)
            .RunAsync(SourcesRequest(["policy"], SubA, SubB), firstSink);
        Assert.Equal("Completed", firstSink.Status);
        Assert.Single(first.FailedSourceGroups);
        Assert.Equal(SourceState.Unavailable, ScanManifest.Build("Completed", first, 2).Sources!["policy"].State);

        var handler = GovernanceHandler();
        var sink = new GovernanceSink();
        var resumed = await new ScanEngine(Graph(handler), groupSize: 1)
            .RunAsync(SourcesRequest(["policy"], SubA, SubB), sink, resume: ScanCheckpoint.FromJson(first.ToJson()));

        var body = Assert.Single(handler.RequestBodies);
        Assert.Equal("policy", KindOf(body));
        Assert.Equal(SubB, SubOf(body));
        Assert.Empty(resumed.FailedSourceGroups);
        // The stored checkpoint no longer names the failure either.
        Assert.Empty(Assert.Single(sink.SourcePages).Stored.FailedSourceGroups);
        Assert.Equal(2, resumed.SourceRows["policy"]);
        Assert.Equal(new SourceState(SourceState.Collected, 2, null), ScanManifest.Build("Completed", resumed, 2).Sources!["policy"]);
    }

    [Fact]
    public async Task ContentHashAndResourceCount_UnchangedBySources()
    {
        var plain = await new ScanEngine(Graph(GovernanceHandler()), groupSize: 1)
            .RunAsync(SourcesRequest(null, SubA, SubB), new GovernanceSink());
        var withSourcesSink = new GovernanceSink();
        var withSources = await new ScanEngine(Graph(GovernanceHandler((kind, sub) => kind == "policy" && sub == SubA ? Forbidden() : null)), groupSize: 1)
            .RunAsync(SourcesRequest(GovernanceSourcesAll, SubA, SubB), withSourcesSink);

        Assert.Equal("Completed", withSourcesSink.Status);
        Assert.Equal(plain.ContentHash, withSources.ContentHash);
        Assert.Equal(plain.Rows, withSources.Rows);
        Assert.Equal(plain.CompletedQueries.Order(StringComparer.Ordinal), withSources.CompletedQueries.Where(k => !k.Contains(':')).Order(StringComparer.Ordinal));
        Assert.Equal(plain.AnyTruncated, withSources.AnyTruncated);
        Assert.Empty(withSources.FailedQueries);
        Assert.Equal(plain.RequestKey, withSources.RequestKey); // sources are not part of the request key
    }

    [Fact]
    public async Task Resume_OnlySourceGroupsPending_KeepsTheContentHash()
    {
        var uninterrupted = await new ScanEngine(Graph(GovernanceHandler()), groupSize: 1)
            .RunAsync(SourcesRequest(["advisor"], SubA), new GovernanceSink());

        var firstSink = new GovernanceSink();
        await new ScanEngine(Graph(GovernanceHandler((kind, _) => kind == "advisor" ? Forbidden() : null)), groupSize: 1)
            .RunAsync(SourcesRequest(["advisor"], SubA), firstSink);
        // The checkpoint a run leaves when it is killed after storing its inventory page, while
        // the Advisor query is still in flight: the inventory group complete, the source group
        // neither complete nor failed, and no content hash (that is set when a run finishes).
        var json = System.Text.Json.Nodes.JsonNode.Parse(firstSink.InventoryCheckpointJson!)!.AsObject();
        Assert.Null(json["contentHash"]);
        json["failedSourceGroups"] = new System.Text.Json.Nodes.JsonObject();

        var handler = GovernanceHandler();
        var resumed = await new ScanEngine(Graph(handler), groupSize: 1)
            .RunAsync(SourcesRequest(["advisor"], SubA), new GovernanceSink(), resume: ScanCheckpoint.FromJson(json.ToJsonString()));

        // No inventory worker ran, yet the hash still describes the inventory the scan holds.
        Assert.Equal(["advisor"], handler.RequestBodies.Select(KindOf).ToArray());
        Assert.NotNull(uninterrupted.ContentHash);
        Assert.Equal(uninterrupted.ContentHash, resumed.ContentHash);
        Assert.Equal(uninterrupted.Rows, resumed.Rows);
    }

    [Fact]
    public async Task OldCheckpointWithoutSourceFields_StillResumes()
    {
        var first = await new ScanEngine(Graph(GovernanceHandler((kind, sub) => kind == "inventory" && sub == SubB ? Forbidden() : null)), groupSize: 1)
            .RunAsync(SourcesRequest(null, SubA, SubB), new GovernanceSink());
        Assert.Single(first.FailedQueries);

        // The checkpoint as it was written before governance sources existed.
        var json = System.Text.Json.Nodes.JsonNode.Parse(first.ToJson())!.AsObject();
        foreach (var added in new[] { "requestedSources", "sourceRows", "failedSourceGroups", "defenderPlans", "totalGroups", "groupHashes" })
            Assert.True(json.Remove(added), added);
        var old = ScanCheckpoint.FromJson(json.ToJsonString())!;
        Assert.Empty(old.RequestedSources);
        Assert.Empty(old.SourceRows);
        Assert.Empty(old.FailedSourceGroups);
        Assert.Empty(old.DefenderPlans);

        var handler = GovernanceHandler();
        var sink = new GovernanceSink();
        var resumed = await new ScanEngine(Graph(handler), groupSize: 1)
            .RunAsync(SourcesRequest(GovernanceSourcesAll, SubA, SubB), sink, resume: old);

        var body = Assert.Single(handler.RequestBodies);
        Assert.Equal(("inventory", SubB), (KindOf(body), SubOf(body)));
        Assert.Equal("Completed", sink.Status);
        Assert.Equal(2, resumed.CompletedQueries.Count);
        Assert.Empty(resumed.FailedQueries);
        Assert.Empty(resumed.RequestedSources);
        Assert.All(ScanManifest.Build("Completed", resumed, 2).Sources!.Values, s => Assert.Equal(SourceState.NotRequested, s.State));
        // SubA's rows were stored by a run that kept no per-group hash, so they cannot be hashed
        // again: no hash rather than one that leaves them out.
        Assert.Null(resumed.ContentHash);
    }

    private sealed class ListProgress : IProgress<ScanProgress>
    {
        public readonly ConcurrentQueue<ScanProgress> Reports = new();
        public void Report(ScanProgress value) => Reports.Enqueue(value);
    }

    [Fact]
    public async Task Progress_TotalsIncludeSourceWorkers()
    {
        var progress = new ListProgress();
        await new ScanEngine(Graph(GovernanceHandler()), groupSize: 1)
            .RunAsync(SourcesRequest(["advisor", "defender"], SubA, SubB), new GovernanceSink(), progress: progress);

        Assert.Equal(6, progress.Reports.Count); // one report per worker: 2 groups x (inventory + 2 sources)
        Assert.All(progress.Reports, p => Assert.Equal(6, p.QueriesTotal));
    }

    [Fact]
    public async Task UnknownSource_IsRefusedBeforeAnythingRuns()
    {
        var handler = GovernanceHandler();
        var sink = new GovernanceSink();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => new ScanEngine(Graph(handler), groupSize: 1)
            .RunAsync(SourcesRequest(["advisor", "attackpaths"], SubA), sink));

        Assert.Contains("attackpaths", error.Message);
        Assert.Empty(handler.RequestBodies);
        Assert.Null(sink.Started);
    }

    [Fact]
    public async Task Checkpoint_SourceFieldsRoundTrip()
    {
        var checkpoint = await new ScanEngine(Graph(GovernanceHandler((kind, sub) => kind == "policy" ? Forbidden() : null)), groupSize: 1)
            .RunAsync(SourcesRequest(GovernanceSourcesAll, SubA, SubB), new GovernanceSink());

        var restored = ScanCheckpoint.FromJson(checkpoint.ToJson())!;

        Assert.Equal(checkpoint.RequestedSources, restored.RequestedSources);
        Assert.Equal(checkpoint.SourceRows, restored.SourceRows);
        Assert.Equal(checkpoint.FailedSourceGroups, restored.FailedSourceGroups);
        Assert.Equal(checkpoint.DefenderPlans, restored.DefenderPlans);
        Assert.Equal(4, restored.DefenderPlans.Count);
        Assert.Equal(2, restored.TotalGroups);
    }

    private static readonly IReadOnlyList<string> GovernanceSourcesAll = GovernanceSources.All;

    private sealed class AsyncHandler(Func<string, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            await responder(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>Holds each page it stores for a while and notes whether the checkpoint it was
    /// handed changed meanwhile - a real sink serializes it then, and a failure added mid-way
    /// broke that serialization ("Collection was modified").</summary>
    private sealed class WatchingSink : IScanSink
    {
        public readonly TaskCompletionSource Persisting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ChangedWhileStoring;
        public string? Status;

        public Task ScanStartedAsync(ScanRequest request, ScanCheckpoint checkpoint, bool isResume, CancellationToken ct) => Task.CompletedTask;
        public Task PagePersistedAsync(string queryKey, IReadOnlyList<InventoryItem> page, ScanCheckpoint checkpoint, CancellationToken ct) => WatchAsync(checkpoint);
        public Task SourcePagePersistedAsync(string source, string queryKey, IReadOnlyList<GovernanceFinding> findings, ScanCheckpoint checkpoint, CancellationToken ct) => WatchAsync(checkpoint);

        private async Task WatchAsync(ScanCheckpoint checkpoint)
        {
            var before = checkpoint.ToJson();
            Persisting.TrySetResult();
            for (var waited = 0; waited < 300 && !ChangedWhileStoring; waited += 10)
            {
                await Task.Delay(10);
                ChangedWhileStoring = checkpoint.ToJson() != before;
            }
        }

        public Task ScanFinishedAsync(string status, ScanCheckpoint checkpoint, CancellationToken ct)
        {
            Status = status;
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("policy")]    // a source failing while an inventory page is stored
    [InlineData("inventory")] // an inventory group failing while a source page is stored
    public async Task Failure_IsNotWrittenIntoACheckpointWhileTheSinkStoresIt(string failing)
    {
        var sink = new WatchingSink();
        var handler = new AsyncHandler(async body =>
        {
            if (KindOf(body) != failing)
                return Arg(KindOf(body) == "inventory" ? Page([$"/subscriptions/{SubA}/resourceGroups/rg-app/providers/Microsoft.Test/widgets/w1"]) : Rows(false, PolicyRow(SubA, "state1")));
            await sink.Persisting.Task; // fail only once the other page is being stored
            return Forbidden();
        });
        var engine = new ScanEngine(new ResourceGraphClient(new HttpClient(handler), new FakeCredential(), null, (_, _) => Task.CompletedTask), groupSize: 1, maxParallel: 2);

        var checkpoint = await engine.RunAsync(SourcesRequest(["policy"], SubA), sink);

        Assert.False(sink.ChangedWhileStoring);
        Assert.Single(failing == "policy" ? checkpoint.FailedSourceGroups : checkpoint.FailedQueries);
        Assert.Equal(failing == "policy" ? "Completed" : "Partial", sink.Status);
    }
}

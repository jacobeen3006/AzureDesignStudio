using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AzureDesignStudio.Discovery.Iac;
using Azure.Core;
using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.ResourceGraph;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// v2 plan 12.3/[60][61]: the stack engine builds a canonical PUT body bound by the same
/// sha256 identity the approval gate uses, refuses delete-flavored unmanage policy until the
/// Phase 13 gates ship, reads managed ids from a stack GET without silent drops, and answers
/// the stack-out-of-sync guard case-insensitively and deterministically.
/// </summary>
public class DeploymentStackTests
{
    private const string Template = """
        {"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","resources":[]}
        """;

    private static readonly StackPolicy DefaultPolicy = new(StackUnmanageAction.Detach);

    private static string Codes(DeploymentStackBody body) =>
        string.Join(",", body.Diagnostics.Select(d => d.Code));

    [Fact]
    public void Build_IsDeterministic_AndParameterOrderIrrelevant()
    {
        var a = DeploymentStack.Build("stack-a", "westeurope", Template, DefaultPolicy, """{"b":{"value":2},"a":{"value":1}}""");
        var b = DeploymentStack.Build("stack-a", "westeurope", Template, DefaultPolicy, """{"a":{"value":1},"b":{"value":2}}""");

        Assert.True(a.Success, Codes(a));
        Assert.Equal(a.PlanHash, b.PlanHash);
        // Canonical order binds the hash; the emitted body stays first-appearance ordered.
        Assert.Contains("\"b\":", a.Json);
        Assert.NotEqual(a.Json, b.Json);
    }

    [Fact]
    public void Build_HashChangesWithEveryIdentityInput()
    {
        var baseBody = DeploymentStack.Build("stack-a", "westeurope", Template, DefaultPolicy);
        var renamed = DeploymentStack.Build("stack-b", "westeurope", Template, DefaultPolicy);
        var moved = DeploymentStack.Build("stack-a", "northeurope", Template, DefaultPolicy);
        var edited = DeploymentStack.Build("stack-a", "westeurope", Template.Replace("1.0.0.0", "2.0.0.0"), DefaultPolicy);
        var repolicyed = DeploymentStack.Build("stack-a", "westeurope", Template, new StackPolicy(StackUnmanageAction.Ignore));

        Assert.StartsWith("sha256:", baseBody.PlanHash);
        AssertDistinct(baseBody.PlanHash, renamed.PlanHash, moved.PlanHash, edited.PlanHash, repolicyed.PlanHash);
    }

    private static void AssertDistinct(params string[] values)
    {
        Assert.Equal(values.Length, values.Distinct().Count());
    }

    [Theory]
    [InlineData("", "StackNameInvalid")]
    [InlineData("bad name!", "StackNameInvalid")]
    [InlineData("x", null)]
    public void Build_ValidatesName(string name, string? expectedCode)
    {
        var body = DeploymentStack.Build(name, "westeurope", Template, DefaultPolicy);
        Assert.Equal(expectedCode is null, body.Diagnostics.All(d => d.Severity != ArmDiagnosticSeverity.Error));
        if (expectedCode is not null)
            Assert.Contains(expectedCode, Codes(body));
    }

    [Fact]
    public void Build_RejectsMissingLocationAndMalformedTemplate()
    {
        var noLoc = DeploymentStack.Build("s", " ", Template, DefaultPolicy);
        Assert.Contains("StackLocationMissing", Codes(noLoc));

        var badJson = DeploymentStack.Build("s", "westeurope", "{oops", DefaultPolicy);
        Assert.Contains("StackTemplateMalformed", Codes(badJson));
        // A rejected input still yields parseable bytes (the hash step runs unconditionally):
        // the omitted template leaves no dangling comma behind.
        using var parsed = JsonDocument.Parse(badJson.Json);
        Assert.False(parsed.RootElement.GetProperty("properties").TryGetProperty("template", out _));

        var notObject = DeploymentStack.Build("s", "westeurope", "[1,2]", DefaultPolicy);
        Assert.Contains("StackTemplateMalformed", Codes(notObject));

        var badParams = DeploymentStack.Build("s", "westeurope", Template, DefaultPolicy, "null");
        Assert.Contains("StackParametersMalformed", Codes(badParams));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("resourceGroup")]
    [InlineData("deployment")]
    public void Build_RefusesDeleteActionsUntilPhase13(string slot)
    {
        var policy = slot switch
        {
            "missing" => new StackPolicy(StackUnmanageAction.Delete),
            "resourceGroup" => new StackPolicy(StackUnmanageAction.Detach, StackUnmanageAction.Delete),
            _ => new StackPolicy(StackUnmanageAction.Detach, StackUnmanageAction.Detach, StackUnmanageAction.Delete),
        };

        var body = DeploymentStack.Build("s", "westeurope", Template, policy);

        Assert.False(body.Success);
        var diag = Assert.Single(body.Diagnostics, d => d.Code == "StackDeleteActionLocked");
        Assert.Contains($"actionOnUnmanage.{slot}", diag.Message);
        Assert.Contains("13", diag.Message);
    }

    [Fact]
    public void Build_EnforcesDenySettingsContract()
    {
        var emptyLocks = DeploymentStack.Build("s", "westeurope", Template,
            DefaultPolicy with { DenySettings = new StackDenySettings(true, []) });
        Assert.Contains("StackDenyLocksRequired", Codes(emptyLocks));

        var locked = DeploymentStack.Build("s", "westeurope", Template,
            DefaultPolicy with { DenySettings = new StackDenySettings(true, [StackDenyLock.NotDeletionAndChildUsage, StackDenyLock.NotDeletion]) });
        Assert.True(locked.Success, Codes(locked));
        // Locks are emitted in a stable order regardless of input order.
        Assert.Contains("""locks":["NotDeletion","NotDeletionAndChildUsage"]""", locked.Json);
    }

    [Fact]
    public void Build_EmitsArmStackShape()
    {
        var body = DeploymentStack.Build("stack-a", "westeurope", Template,
            DefaultPolicy with { Missing = StackUnmanageAction.Ignore });
        Assert.True(body.Success, Codes(body));

        using var doc = JsonDocument.Parse(body.Json);
        var root = doc.RootElement;
        Assert.Equal("stack-a", root.GetProperty("name").GetString());
        Assert.Equal("westeurope", root.GetProperty("location").GetString());
        Assert.Equal("Microsoft.Resources/deploymentStacks", root.GetProperty("type").GetString());
        Assert.Equal(DeploymentStack.ApiVersion, root.GetProperty("apiVersion").GetString());

        var actions = root.GetProperty("properties").GetProperty("policy").GetProperty("actionOnUnmanage");
        Assert.Equal("Detach", actions.GetProperty("resourceGroup").GetString());
        Assert.Equal("Detach", actions.GetProperty("deployment").GetString());
        Assert.Equal("Ignore", actions.GetProperty("missing").GetString());
        Assert.Equal("1.0.0.0", root.GetProperty("properties").GetProperty("template").GetProperty("contentVersion").GetString());
        Assert.False(root.GetProperty("properties").TryGetProperty("denySettings", out _));
    }

    [Fact]
    public void ReadManagedResources_HonestAboutEveryPayloadShape()
    {
        var (ids, ok) = DeploymentStack.ReadManagedResources(
            """{"properties":{"managedResources":[{"resourceId":"/subscriptions/s1/resourceGroups/g1/providers/Microsoft.Storage/storageAccounts/stone"},{"resourceId":" /x/y "}]}}""");
        Assert.Equal(["/subscriptions/s1/resourceGroups/g1/providers/Microsoft.Storage/storageAccounts/stone", "/x/y"], ids);
        Assert.Empty(ok);

        (ids, ok) = DeploymentStack.ReadManagedResources("""{"properties":{"managedResources":[{"resourceId":"/x"},]},"}""");
        Assert.Empty(ids);
        Assert.Contains("StackPayloadMalformed", string.Join(",", ok.Select(d => d.Code)));
        (ids, ok) = DeploymentStack.ReadManagedResources(
            """{"properties":{"managedResources":[{"resourceId":"/subs/a"},{"name":"no-id"},{"resourceId":42}]}}""");
        Assert.Equal(["/subs/a"], ids);
        Assert.Equal(2, ok.Count);
        Assert.All(ok, d => Assert.Equal("StackManagedResourceInvalid", d.Code));
        Assert.Contains("managedResources[1]", ok[0].Message);
        Assert.Contains("managedResources[2]", ok[1].Message);

        (_, var missing) = DeploymentStack.ReadManagedResources("""{"properties":{}}""");
        Assert.Contains("StackManagedResourcesMissing", missing.Select(d => d.Code));

        var (emptyIds, emptyDiags) = DeploymentStack.ReadManagedResources("""{"properties":{"managedResources":[]}}""");
        Assert.Empty(emptyIds);
        Assert.Empty(emptyDiags);
    }

    [Fact]
    public void CheckDrift_ComparesArmIdsCaseInsensitively_AndReportsEveryDivergence()
    {
        var report = DeploymentStack.CheckDrift(
            ["/SUBSCRIPTIONS/s1/resourceGroups/g1", "/subscriptions/s1/missing"],
            ["/subscriptions/s1/resourceGroups/g1", "/subscriptions/s1/untracked"]);

        Assert.True(report.IsOutOfSync);
        Assert.Equal(1, report.SyncedCount);
        Assert.Equal(1, report.MissingCount);
        Assert.Equal(1, report.UntrackedCount);
        // Stacked first keeps first-appearance casing; rows sort ordinal by the lowercased key.
        Assert.Equal(
            new[] { "/SUBSCRIPTIONS/s1/resourceGroups/g1", "/subscriptions/s1/missing", "/subscriptions/s1/untracked" }
                .OrderBy(id => id.ToLowerInvariant(), System.StringComparer.Ordinal)
                .ToList(),
            report.Rows.Select(r => r.Id).ToList());
        Assert.Equal(StackDriftKind.Synced, report.Rows.Single(r => r.Id.ToLowerInvariant().Contains("resourcegroups")).Kind);
        Assert.Equal(StackDriftKind.Missing, report.Rows.Single(r => r.Id.Contains("missing")).Kind);
        Assert.Equal(StackDriftKind.Untracked, report.Rows.Single(r => r.Id.Contains("untracked")).Kind);
    }

    [Fact]
    public void CheckDrift_InSyncStackAndNoiseAreQuiet()
    {
        var same = DeploymentStack.CheckDrift(["/a", "/b"], ["/A", " /b ", "/b"]);
        Assert.False(same.IsOutOfSync);
        Assert.Equal(2, same.SyncedCount);
        Assert.Equal(2, same.Rows.Count); // live duplicate "/b" adds no row

        var noise = DeploymentStack.CheckDrift(["", "  ", "/a"], [null!, "/a"]);
        Assert.False(noise.IsOutOfSync);
        Assert.Single(noise.Rows);
    }

    // ---- 12.3 execution wiring: the client sends the engine's exact bytes to ARM's stack
    // endpoint and follows the resource LRO; refusals surface as typed statuses (scripted ARM). ----

    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Uris { get; } = [];
        public List<string> Methods { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!.ToString());
            Methods.Add(request.Method.Method);
            Bodies.Add(request.Content is null ? null : request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return Task.FromResult(responder(request, Uris.Count - 1));
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new AccessToken("fake-token", DateTimeOffset.MaxValue));
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
            => new("fake-token", DateTimeOffset.MaxValue);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK,
        string? location = null, string? asyncOperation = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (location is not null) response.Headers.Location = new Uri(location);
        if (asyncOperation is not null) response.Headers.TryAddWithoutValidation("Azure-AsyncOperation", asyncOperation);
        return response;
    }

    private static DeploymentStackClient Client(FakeHandler handler) =>
        new(new ArmTransport(new HttpClient(handler), new FakeCredential(),
            ArmEndpointSet.AzureCloud, (_, _) => Task.CompletedTask));

    [Fact]
    public async Task Client_CreateOrUpdate_SendsEngineBytesAndFollowsAsyncOperationLro()
    {
        var body = DeploymentStack.Build("ads-stack", "westeurope", Template, DefaultPolicy);
        const string asyncUrl = "https://management.azure.com/operationLocations/async-42?api-version=2021-04-01";
        var handler = new FakeHandler((_, call) => call switch
        {
            0 => Json("""{"properties":{"provisioningState":"Creating"}}""", HttpStatusCode.Created, asyncOperation: asyncUrl),
            1 => Json("""{"status":"InProgress"}""", HttpStatusCode.Accepted), // headerless 202 keeps the last poll URI
            _ => Json("""{"name":"ads-stack","properties":{"provisioningState":"Succeeded","managedResources":[{"resourceId":"/subscriptions/sub-1/resourceGroups/rg1/providers/Microsoft.Storage/storageAccounts/sa1"}]}}"""),
        });

        var result = await Client(handler).CreateOrUpdateAsync("sub-1", "rg1", "ads-stack", body.Json);

        Assert.Equal("PUT", handler.Methods[0]);
        Assert.Equal(
            "https://management.azure.com/subscriptions/sub-1/resourcegroups/rg1/providers/Microsoft.Resources/deploymentStacks/ads-stack?api-version=2021-04-01",
            handler.Uris[0]);
        Assert.Equal(body.Json, handler.Bodies[0]); // the engine's exact bytes reach ARM
        Assert.Equal(3, handler.Uris.Count);
        Assert.Equal(asyncUrl, handler.Uris[1]);
        Assert.Equal(asyncUrl, handler.Uris[2]);
        Assert.Contains("Succeeded", result);
    }

    [Fact]
    public async Task Client_CreateOrUpdate_FollowsLocationLroAndGetReadsTheSameUrl()
    {
        const string pollUrl = "https://management.azure.com/operationLocations/loc-7?api-version=2021-04-01";
        var handler = new FakeHandler((_, call) => call switch
        {
            0 => Json("{}", HttpStatusCode.Accepted, location: pollUrl),
            _ => Json("""{"properties":{"provisioningState":"Succeeded","managedResources":[]}}"""),
        });

        var client = Client(handler);
        await client.CreateOrUpdateAsync("sub-1", "rg1", "ads-stack", "{}");
        await client.GetAsync("sub-1", "rg1", "ads-stack");

        Assert.Equal(pollUrl, handler.Uris[1]);
        Assert.Equal("GET", handler.Methods[2]);
        Assert.Equal(handler.Uris[0], handler.Uris[2]);
    }

    [Fact]
    public async Task Client_ArmRefusalSurfacesTypedStatus_AndNeverRetriesTheWrite()
    {
        var handler = new FakeHandler((_, _) =>
            Json("""{"error":{"code":"InvalidTemplateDeployment","message":"nope"}}""", HttpStatusCode.BadRequest));

        var ex = await Assert.ThrowsAsync<ResourceGraphException>(() =>
            Client(handler).CreateOrUpdateAsync("sub-1", "rg1", "ads-stack", "{}"));

        Assert.Equal(400, ex.StatusCode);
        Assert.Single(handler.Uris); // one send: the audit caller decides, the transport never retries a write
    }

    [Fact]
    public void TryParse_WireFormsRejectGuesses()
    {
        Assert.True(DeploymentStack.TryParseAction("detach", out var action) && action == StackUnmanageAction.Detach);
        Assert.True(DeploymentStack.TryParseAction("IGNORE", out action) && action == StackUnmanageAction.Ignore);
        Assert.False(DeploymentStack.TryParseAction("", out _));   // blank: no silent default at the wire
        Assert.False(DeploymentStack.TryParseAction("1", out _));  // a numeric protocol-guess is refused, not Delete
        Assert.False(DeploymentStack.TryParseAction("bogus", out _));
        Assert.False(DeploymentStack.TryParseAction(null, out _));
        Assert.True(DeploymentStack.TryParseDenyLock("notdeletion", out var denyLock) && denyLock == StackDenyLock.NotDeletion);
        Assert.False(DeploymentStack.TryParseDenyLock("5", out _));
    }

    // ---- v2 12.3 closer: bypass rides the hashed body; managed lists parse from both
    // verified shapes; the [61] review compares managed against the planned ids. ----

    [Fact]
    public void Build_BypassMemberRidesInsideHashedBody_AfterParameters()
    {
        var def = DeploymentStack.Build("stack-a", "westeurope", Template, DefaultPolicy);
        var bypass = DeploymentStack.Build("stack-a", "westeurope", Template, DefaultPolicy, "{}", bypassOutOfSyncError: true);
        var bypassNoParams = DeploymentStack.Build("stack-a", "westeurope", Template, DefaultPolicy, bypassOutOfSyncError: true);

        // Golden default: the pre-bypass bytes are byte-identical to the pinned shape.
        Assert.Equal("""{"name":"stack-a","location":"westeurope","type":"Microsoft.Resources/deploymentStacks","apiVersion":"2021-04-01","properties":{"template":{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","resources":[]},"policy":{"actionOnUnmanage":{"resourceGroup":"Detach","deployment":"Detach","missing":"Detach"}}}}""", def.Json);

        Assert.Contains("\"parameters\":{},\"bypassStackOutOfSyncError\":true,\"policy\"", bypass.Json);
        Assert.Contains("\"bypassStackOutOfSyncError\":true,\"policy\"", bypassNoParams.Json);
        // The member rides inside the hashed body: the sha256: approval binds the bypass too.
        Assert.NotEqual(def.PlanHash, bypass.PlanHash);
    }

    [Theory]
    [InlineData("ads-stack", true)]
    [InlineData("a.-_()x9", true)]
    [InlineData("", false)]
    [InlineData("bad name!", false)]
    [InlineData(null, false)]
    public void IsValidStackName_IsTheUrlSafetyGate(string? name, bool expected)
        => Assert.Equal(expected, DeploymentStack.IsValidStackName(name));

    [Fact]
    public void ReadManagedResources_AcceptsBothVerifiedShapes_AndDedupesTheUnion()
    {
        // The list/get newer shape: {id, status, denyStatus}.
        var (ids, diags) = DeploymentStack.ReadManagedResources(
            """{"properties":{"resources":[{"id":"/subs/s/rgs/g/Microsoft.Storage/storageAccounts/sa1","status":"Managed","denyStatus":"NotDefined"}]}}""");
        Assert.Equal(["/subs/s/rgs/g/Microsoft.Storage/storageAccounts/sa1"], ids);
        Assert.Empty(diags);

        // Both keys at once: union deduped by trim+lowercase, first-appearance casing kept.
        (ids, diags) = DeploymentStack.ReadManagedResources(
            """{"properties":{"managedResources":[{"resourceId":"/Subs/S/resourceGroups/G/stone"},{"resourceId":" /shared "}],"resources":[{"id":"/subs/s/resourcegroups/g/STONE"},{"id":"/other"}]}}""");
        Assert.Equal(["/Subs/S/resourceGroups/G/stone", "/shared", "/other"], ids);
        Assert.Empty(diags);

        // Empty array under either key = reported-empty, no diagnostic; neither key = malformed.
        (ids, diags) = DeploymentStack.ReadManagedResources("""{"properties":{"resources":[]}}""");
        Assert.Empty(ids);
        Assert.Empty(diags);
        (_, diags) = DeploymentStack.ReadManagedResources("""{"properties":{}}""");
        Assert.Contains("StackManagedResourcesMissing", diags.Select(d => d.Code));

        // Unusable entries are counted per row, never silently dropped.
        (ids, diags) = DeploymentStack.ReadManagedResources("""{"properties":{"resources":[{"id":42},{"status":"Managed"}]}}""");
        Assert.Empty(ids);
        Assert.Equal(2, diags.Count);
        Assert.All(diags, d => Assert.Equal("StackManagedResourceInvalid", d.Code));
        Assert.Contains("resources[0]", diags[0].Message);
    }

    [Fact]
    public void PlannedManagedResourceIds_TwoLiteralsPlusExpression_YieldsTwoIdsAndOneUnresolved()
    {
        var template = """
            {"resources":[
              {"type":"Microsoft.Storage/storageAccounts","name":"stoneg1"},
              {"type":"Microsoft.Network/virtualNetworks/subnets","name":"vnet1/subnet-a"},
              {"type":"Microsoft.Web/sites","name":"[parameters('n')]"}
            ]}
            """;
        var (ids, diags) = DeploymentStack.PlannedManagedResourceIds(template, "sub-1", "rg1");

        Assert.Equal(
        [
            "/subscriptions/sub-1/resourceGroups/rg1/providers/Microsoft.Network/virtualNetworks/vnet1/subnets/subnet-a",
            "/subscriptions/sub-1/resourceGroups/rg1/providers/Microsoft.Storage/storageAccounts/stoneg1",
        ], ids);
        var unresolved = Assert.Single(diags);
        Assert.Equal("PlannedIdUnresolved", unresolved.Code);
        Assert.Contains("Microsoft.Web/sites/[parameters('n')]", unresolved.Message);
    }

    [Fact]
    public void PlannedManagedResourceIds_FailsClosedOnScopeAndMissingParts_AndSortsOrdinal()
    {
        var template = """
            {"resources":[
              {"type":"Microsoft.Web/sites","name":"web2","scope":"Microsoft.Network/virtualNetworks/vnet1"},
              {"type":"Microsoft.Web/sites","name":"web1","scope":"/subscriptions/other-sub/resourceGroups/other-rg"},
              {"name":"no-type"},
              {"type":"Microsoft.Storage/storageAccounts","name":"Baker"},
              {"type":"Microsoft.Storage/storageAccounts","name":"able"}
            ]}
            """;
        var (ids, diags) = DeploymentStack.PlannedManagedResourceIds(template, "sub-1", "rg1");

        Assert.Equal(
        [
            "/subscriptions/sub-1/resourceGroups/rg1/providers/Microsoft.Storage/storageAccounts/Baker",
            "/subscriptions/sub-1/resourceGroups/rg1/providers/Microsoft.Storage/storageAccounts/able",
            "/subscriptions/sub-1/resourceGroups/rg1/providers/Microsoft.Web/sites/web2",
        ], ids); // ordinal sort: uppercase sorts before lowercase; template casing kept
        var off = Assert.Single(diags, d => d.Code == "PlannedIdOffScope");
        Assert.Contains("Microsoft.Web/sites/web1", off.Message);
        Assert.Contains(diags, d => d.Code == "PlannedIdUnresolved" && d.Message.Contains("no-type"));

        // A template that is not JSON plans nothing and says so.
        (_, diags) = DeploymentStack.PlannedManagedResourceIds("{oops", "sub-1", "rg1");
        Assert.Contains("PlannedIdUnresolved", diags.Select(d => d.Code));
    }

    [Fact]
    public void DiffManagedAgainstPlan_CountsCaseInsensitively_AndSortsBothLists()
    {
        var diff = DeploymentStack.DiffManagedAgainstPlan(
            ["/SUBS/s/stone", "/subs/s/keep", "/subs/s/gone"],
            ["/subs/s/KEEP", "/subs/s/new-one", "/subs/s/new-two"]);

        Assert.Equal(1, diff.AlreadyManagedCount);
        Assert.Equal(["/subs/s/new-one", "/subs/s/new-two"], diff.TakingOver);
        Assert.Equal(["/subs/s/gone", "/SUBS/s/stone"], diff.Releasing);
    }

    [Fact]
    public void DiffManagedAgainstPlan_IsDeterministicUnderShuffle()
    {
        var a = DeploymentStack.DiffManagedAgainstPlan(["/b", "/a", "/c"], ["/c", "/x", "/Y"]);
        var b = DeploymentStack.DiffManagedAgainstPlan(["/c", "/b", "/a"], ["/Y", "/x", "/c"]);

        Assert.Equal(a.AlreadyManagedCount, b.AlreadyManagedCount);
        Assert.Equal(a.TakingOver, b.TakingOver);
        Assert.Equal(a.Releasing, b.Releasing);
    }

    [Fact]
    public async Task Client_ListAtSubscription_PinsTheVerifiedUrl_AndReturnsElements()
    {
        var handler = new FakeHandler((_, _) => Json("""{"value":[{"name":"s1","id":"/subscriptions/sub-1/resourceGroups/rg1/providers/Microsoft.Resources/deploymentStacks/s1","location":"westeurope","properties":{"resources":[{"id":"/x"}]}}]}"""));

        var elements = await Client(handler).ListAtSubscriptionAsync("sub-1");

        Assert.Equal(
            "https://management.azure.com/subscriptions/sub-1/providers/Microsoft.Resources/deploymentStacks?api-version=2024-03-01",
            handler.Uris[0]);
        Assert.Single(handler.Uris);
        Assert.Single(elements);
        Assert.Equal("s1", elements[0].GetProperty("name").GetString());
    }
}

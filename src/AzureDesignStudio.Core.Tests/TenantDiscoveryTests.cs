using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.Preflight;
using AzureDesignStudio.Discovery.ResourceGraph;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// Scope tree (7.3) and permission preflight (7.2) replayed against scripted ARM/ARG
/// responses - enrollment detection and role explanations without a live tenant.
/// </summary>
public class TenantDiscoveryTests
{
    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> router) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(router(request));
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new AccessToken("fake", DateTimeOffset.MaxValue));
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
            => new("fake", DateTimeOffset.MaxValue);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (TenantInventory Inventory, PermissionPreflight Preflight) Create(
        Func<HttpRequestMessage, HttpResponseMessage> router)
    {
        var transport = new ArmTransport(new HttpClient(new RoutingHandler(router)), new FakeCredential(), ArmEndpointSet.AzureCloud, NoDelay);
        var graph = new ResourceGraphClient(new HttpClient(new RoutingHandler(router)), new FakeCredential(), delay: NoDelay);
        return (new TenantInventory(transport, graph), new PermissionPreflight(transport, graph));
    }

    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    private static string ArgRows(params (string Id, string Name, string Sub)[] rows) =>
        """{"data":[""" + string.Join(",", rows.Select(r => $$"""{"id":"{{r.Id}}","name":"{{r.Name}}","subscriptionId":"{{r.Sub}}"}""")) + """],"count":0,"totalRecords":0,"resultTruncated":"false"}""";

    private const string Subscriptions = """
        {"value":[
          {"subscriptionId":"s1","displayName":"Prod","tenantId":"t1","state":"Enabled"},
          {"subscriptionId":"s2","displayName":"Dev","tenantId":"t1","state":"Enabled"},
          {"subscriptionId":"s3","displayName":"Legacy","tenantId":"t1","state":"Enabled"}]}
        """;

    private const string ManagementGroups = """
        {"value":[
          {"name":"mg-root","properties":{"displayName":"Root","children":[{"name":"mg-dev","type":"/managementGroups"},{"name":"s1","type":"/subscriptions"}]}},
          {"name":"mg-dev","properties":{"displayName":"Dev","parent":{"id":"/providers/Microsoft.Management/managementGroups/mg-root"},"children":[{"name":"s2","type":"/subscriptions"}]}}]}
        """;

    private static HttpResponseMessage TenantRouter(HttpRequestMessage req)
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("/subscriptions?")) return Json(Subscriptions);
        if (url.Contains("managementGroups")) return Json(ManagementGroups);
        if (url.Contains("Microsoft.ResourceGraph"))
            return Json(ArgRows(("/rg/rg-a", "rg-a", "s1"), ("/rg/rg-b", "rg-b", "s2"))); // s3 unenrolled
        return Json("""{"value":[]}""");
    }

    [Fact]
    public async Task ScopeTree_NestsTenantMgSubscriptionRg_AndFlagsUnenrolled()
    {
        var (inventory, _) = Create(TenantRouter);
        var root = await inventory.BuildScopeTreeAsync();

        Assert.Equal(ScopeKind.Tenant, root.Kind);
        Assert.Equal("t1", root.Id);

        // mg-root at top; mg-dev nested under it; unlinked sub s3 hangs at tenant level
        var mgRoot = Assert.Single(root.Nodes, n => n.Kind == ScopeKind.ManagementGroup);
        Assert.Equal("Root", mgRoot.Name);
        Assert.Equal(["mg-dev"], mgRoot.Nodes.Where(n => n.Kind == ScopeKind.ManagementGroup).Select(n => n.Id));

        var prod = mgRoot.Nodes.Single(n => n.Kind == ScopeKind.Subscription);
        Assert.Equal("Prod", prod.Name);
        Assert.True(prod.Enrolled);
        Assert.Equal(["rg-a"], prod.Nodes.Select(n => n.Name));

        var mgDev = mgRoot.Nodes.Single(n => n.Kind == ScopeKind.ManagementGroup);
        var dev = Assert.Single(mgDev.Nodes);
        Assert.Equal("Dev", dev.Name);
        Assert.Equal(["rg-b"], dev.Nodes.Select(n => n.Name));

        var legacy = root.Nodes.Single(n => n.Kind == ScopeKind.Subscription);
        Assert.Equal("Legacy", legacy.Name);
        Assert.False(legacy.Enrolled); // in ARM, silent in ARG
    }

    [Fact]
    public async Task ScopeTree_WithoutManagementGroupPermission_StillBuildsFlatTree()
    {
        var (inventory, _) = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("managementGroups"))
                return Json("""{"error":{"code":"AuthorizationFailed","message":"no mgmt access"}}""", HttpStatusCode.Forbidden);
            return TenantRouter(req);
        });

        var root = await inventory.BuildScopeTreeAsync();

        // No MG data: all subs hang under the tenant, enrollment still correct.
        Assert.Equal(3, root.Nodes.Count(n => n.Kind == ScopeKind.Subscription));
        Assert.Single(root.Nodes, n => n.Kind == ScopeKind.Subscription && !n.Enrolled);
    }

    [Fact]
    public async Task Preflight_HealthyTenant_AllSourcesAvailable()
    {
        var (_, preflight) = Create(TenantRouter);
        var report = await preflight.RunAsync("s1");

        Assert.Equal(5, report.Count);
        Assert.All(report, p => Assert.Equal(ProbeStatus.Available, p.Status));
    }

    [Fact]
    public async Task Preflight_MissingPermissions_DisablesOnlyThoseSourcesWithRole()
    {
        var (_, preflight) = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("Microsoft.CostManagement"))
                return Json("""{"error":{"code":"AuthorizationFailed","message":"no cost access"}}""", HttpStatusCode.Forbidden);
            if (url.Contains("Microsoft.Insights"))
                return Json("{}", HttpStatusCode.TooManyRequests); // throttled, not forbidden
            return TenantRouter(req);
        });

        var report = await preflight.RunAsync("s1");

        var cost = report.Single(p => p.Source == "costManagement");
        Assert.Equal(ProbeStatus.NotAvailable, cost.Status);
        Assert.Contains("Cost Management Reader", cost.Detail); // the exact role to request
        Assert.Contains("actual spend instead of retail estimate (phase 9)", cost.Unlocks);

        var activity = report.Single(p => p.Source == "activityLog");
        Assert.Equal(ProbeStatus.AvailableWithCaveat, activity.Status); // throttled != missing permission

        // the rest of the matrix must stay lit
        Assert.Equal(ProbeStatus.Available, report.Single(p => p.Source == "resourceGraph").Status);
        Assert.Equal(ProbeStatus.Available, report.Single(p => p.Source == "subscriptions").Status);
    }

    [Fact]
    public async Task ArmTransport_PostCarriesBodyAndBearerToken()
    {
        HttpRequestMessage? seen = null;
        string? seenBody = null;
        var transport = new ArmTransport(
            new HttpClient(new RoutingHandler(r =>
            {
                seen = r;
                seenBody = r.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("{}");
            })),
            new FakeCredential(), ArmEndpointSet.AzureCloud, NoDelay);

        await transport.PostAsync("https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01", """{"query":"x"}""");

        Assert.NotNull(seen);
        Assert.Equal("Bearer", seen!.Headers.Authorization!.Scheme);
        Assert.Equal("fake", seen.Headers.Authorization!.Parameter);
        Assert.Equal("""{"query":"x"}""", seenBody);
    }
}

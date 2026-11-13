using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Discovery.Iac;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Services;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.SharedModels.Protos;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureDesignStudio.Server.Tests;

internal sealed class FixedCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new("fake-token", DateTimeOffset.MaxValue);

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

internal sealed class AmbientCredential : IAmbientAzureCredential
{
    public TokenCredential Credential { get; } = new FixedCredential();

    public Task<IReadOnlyList<AmbientSubscription>> ListSubscriptionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AmbientSubscription>>([]);
}

/// <summary>Scripted ARM for the stack RPCs: the subscription and resource group reads, the stacks
/// what-if result (PUT answers the finished result, DELETE cleans it up) and the stack PUT.</summary>
internal sealed class ScriptedArm(string subscriptionId, string resourceGroup) : HttpMessageHandler, IHttpClientFactory
{
    public List<string> Calls { get; } = [];
    public string WhatIfResult { get; set; } = "";

    public bool Called(string method, string pathPart) =>
        Calls.Any(c => c.StartsWith(method + " ", StringComparison.Ordinal) && c.Contains(pathPart, StringComparison.Ordinal));

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Calls.Add($"{request.Method.Method} {path}");
        var sub = $"/subscriptions/{subscriptionId}";
        var rg = $"{sub}/resourceGroups/{resourceGroup}";
        string? body = null;
        if (path.Equals(sub, StringComparison.OrdinalIgnoreCase))
            body = "{\"id\":\"" + sub + "\",\"subscriptionId\":\"" + subscriptionId + "\",\"displayName\":\"test\",\"state\":\"Enabled\"}";
        else if (path.Equals(rg, StringComparison.OrdinalIgnoreCase))
            body = "{\"id\":\"" + rg + "\",\"name\":\"" + resourceGroup + "\",\"location\":\"westeurope\",\"properties\":{\"provisioningState\":\"Succeeded\"}}";
        else if (path.Contains("/deploymentStacksWhatIfResults/", StringComparison.OrdinalIgnoreCase))
            body = request.Method == HttpMethod.Delete ? "" : WhatIfResult;
        else if (path.Contains("/deploymentStacks/", StringComparison.OrdinalIgnoreCase))
            body = """{"properties":{"provisioningState":"succeeded"}}""";
        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

/// <summary>The stack RPCs: StackWhatIf issues the stack token and CreateStack consumes only that kind.
/// ARM is scripted at the HttpClient the service takes from IHttpClientFactory.</summary>
public class StackRpcTests
{
    private const string Template =
        """{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","resources":[{"type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","name":"sa1","location":"westeurope","sku":{"name":"Standard_LRS"},"kind":"StorageV2"}]}""";

    private const string ResourceGroup = "rg";
    private readonly DesignDbContext _db = TestData.NewDb();
    private readonly WhatIfTokenGate _gate = new(WhatIfTokenGate.DefaultTtl);
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _subscription = Guid.NewGuid();
    private readonly ScriptedArm _arm;

    public StackRpcTests()
    {
        _arm = new ScriptedArm(_subscription.ToString(), ResourceGroup)
        {
            WhatIfResult = WhatIfBody(Change("Microsoft.Storage/storageAccounts/sa1", "create")),
        };
    }

    private Grpc.Core.ServerCallContext Me => TestServerCallContext.ForUser(_user, "Ada");

    private DeployService Service => new(_db, NullLogger<DeployService>.Instance, new FakeCryptoService(), _gate, _arm,
        new AmbientCredential());

    private string Id(string tail) =>
        $"/subscriptions/{_subscription}/resourceGroups/{ResourceGroup}/providers/{tail}";

    private string Change(string tail, string type, string certainty = "definite", string? extra = null) =>
        "{\"id\":\"" + Id(tail) + "\",\"type\":\"Microsoft.Storage/storageAccounts\",\"changeType\":\"" + type
        + "\",\"changeCertainty\":\"" + certainty + "\"" + (extra is null ? "" : "," + extra) + "}";

    private static string WhatIfBody(params string[] changes) =>
        """{"properties":{"provisioningState":"succeeded","changes":{"resourceChanges":[""" + string.Join(",", changes) + "]}}}";

    private StackWhatIfRequest PreviewRequest(string denyMode = "") => new()
    {
        SubscriptionId = _subscription.ToString(),
        ResourceGroupName = ResourceGroup,
        StackName = "ads-stack",
        ArmTemplate = Template,
        Parameters = "{}",
        ResourcesAction = "detach",
        DenyMode = denyMode,
    };

    private CreateStackRequest CreateRequest(StackWhatIfResponse preview, string denyMode = "") => new()
    {
        SubscriptionId = _subscription.ToString(),
        ResourceGroupName = ResourceGroup,
        StackName = "ads-stack",
        ArmTemplate = Template,
        Parameters = "{}",
        ResourcesAction = "detach",
        DenyMode = denyMode,
        WhatIfToken = preview.WhatIfToken,
        PlanHash = preview.PlanHash,
    };

    private async Task Approve(string planHash) =>
        Assert.Equal(200, (await Service.ApprovePlan(new PlanApprovalRequest { PlanHash = planHash }, Me)).StatusCode);

    [Fact]
    public async Task StackWhatIf_ReturnsDetachesAndToken()
    {
        _arm.WhatIfResult = WhatIfBody(
            Change("Microsoft.Storage/storageAccounts/new1", "create"),
            Change("Microsoft.Storage/storageAccounts/old1", "detach"),
            Change("Microsoft.Storage/storageAccounts/mod1", "modify",
                extra: "\"denyStatusChange\":{\"before\":\"none\",\"after\":\"denyDelete\"}"));

        var response = await Service.StackWhatIf(PreviewRequest(), Me);

        Assert.True(response.StatusCode == 200, response.ErrorMessage);
        Assert.False(string.IsNullOrEmpty(response.WhatIfToken));
        Assert.StartsWith("sha256:", response.PlanHash);
        Assert.Equal((1, 1, 1, 0), (response.Creates, response.Modifies, response.Detaches, response.Deletes));
        Assert.Equal("", response.IndeterminacyReason);
        using var rows = JsonDocument.Parse(response.ChangesJson);
        Assert.Contains(rows.RootElement.EnumerateArray(), r => r.GetProperty("changeType").GetString() == "detach"
            && r.GetProperty("resourceId").GetString()!.EndsWith("/old1"));
        Assert.Contains(rows.RootElement.EnumerateArray(), r => r.TryGetProperty("denyAfter", out var a) && a.GetString() == "denyDelete");
        Assert.True(_arm.Called("PUT", "/deploymentStacksWhatIfResults/"));
        Assert.True(_arm.Called("DELETE", "/deploymentStacksWhatIfResults/"));
        Assert.False(_arm.Called("PUT", "/deploymentStacks/"));
    }

    [Fact]
    public async Task StackWhatIf_DeleteAction_IsRefusedBeforeAnyArmCall()
    {
        var request = PreviewRequest();
        request.ResourcesAction = "delete";

        var response = await Service.StackWhatIf(request, Me);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("StackDeleteActionLocked", response.ErrorMessage);
        Assert.Equal("", response.WhatIfToken);
        Assert.Empty(_arm.Calls);
    }

    [Fact]
    public async Task StackWhatIf_IndeterminateResult_ReturnsTheReasonAndIssuesNoToken()
    {
        _arm.WhatIfResult = WhatIfBody(Change("Microsoft.Storage/storageAccounts/sa1", "modify", certainty: "potential"));

        var response = await Service.StackWhatIf(PreviewRequest(), Me);

        Assert.Equal(200, response.StatusCode);
        Assert.NotEqual("", response.IndeterminacyReason);
        Assert.Equal("", response.WhatIfToken);
        Assert.Contains(response.Caveats, c => c.Contains(response.IndeterminacyReason));
    }

    [Fact]
    public async Task StackWhatIf_ProtectedResource_IsFlaggedAndIssuesNoToken()
    {
        await Service.SetGuardrailPolicy(new GuardrailPolicyRequest { Json = """{"types":["Microsoft.Storage/storageAccounts"]}""" }, Me);

        var response = await Service.StackWhatIf(PreviewRequest(), Me);

        Assert.Equal(200, response.StatusCode);
        Assert.True(response.GuardrailBlocked);
        Assert.Single(response.GuardrailFlags);
        Assert.Equal("", response.WhatIfToken);
    }

    [Fact]
    public async Task StackWhatIf_TagRuleCannotBeChecked_HoldsBackChangesToExistingResources()
    {
        await Service.SetGuardrailPolicy(new GuardrailPolicyRequest { Json = """{"tags":[{"key":"keep"}]}""" }, Me);
        _arm.WhatIfResult = WhatIfBody(Change("Microsoft.Storage/storageAccounts/sa1", "modify"));

        var response = await Service.StackWhatIf(PreviewRequest(), Me);

        Assert.True(response.GuardrailBlocked);
        Assert.Equal("", response.WhatIfToken);

        _arm.WhatIfResult = WhatIfBody(Change("Microsoft.Storage/storageAccounts/sa1", "create"));
        Assert.NotEqual("", (await Service.StackWhatIf(PreviewRequest(), Me)).WhatIfToken);
    }

    [Fact]
    public async Task CreateStack_WithStackToken_Proceeds()
    {
        var preview = await Service.StackWhatIf(PreviewRequest(), Me);
        await Approve(preview.PlanHash);

        var response = await Service.CreateStack(CreateRequest(preview), Me);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("succeeded", response.StackState);
        Assert.True(_arm.Called("PUT", "/deploymentStacks/ads-stack"));
        Assert.Empty(_db.AdsPlanApprovals);
    }

    [Fact]
    public async Task CreateStack_WithDeploymentWhatIfToken_IsBlocked()
    {
        var preview = await Service.StackWhatIf(PreviewRequest(), Me);
        await Approve(preview.PlanHash);
        var plan = DeployPlanArtifact.Create(_subscription.ToString(), ResourceGroup, Template, "{}");
        var deploymentToken = _gate.Issue(_subscription.ToString(), ResourceGroup, Template, "{}",
            new WhatIfSummary { Status = "Succeeded", Changes = [new WhatIfChange(WhatIfChangeKind.Create, "/x", null, null, 0)] },
            plan.CanonicalJson, plan.PlanHash);
        var request = CreateRequest(preview);
        request.WhatIfToken = deploymentToken;

        var response = await Service.CreateStack(request, Me);

        Assert.Equal(400, response.StatusCode);
        Assert.StartsWith("WHAT_IF_PAYLOAD_CHANGED", response.ErrorMessage);
        Assert.False(_arm.Called("PUT", "/deploymentStacks/ads-stack"));
        Assert.Single(_db.AdsPlanApprovals);
        // Neither token was spent by the refusal: the stack token still deploys.
        Assert.Equal(200, (await Service.CreateStack(CreateRequest(preview), Me)).StatusCode);
    }

    [Fact]
    public async Task CreateStack_PolicyChangedAfterPreview_IsBlocked()
    {
        var preview = await Service.StackWhatIf(PreviewRequest(), Me);
        await Approve(preview.PlanHash);

        var response = await Service.CreateStack(CreateRequest(preview, denyMode: "denyDelete"), Me);

        Assert.Equal(400, response.StatusCode);
        Assert.StartsWith("WHAT_IF_PAYLOAD_CHANGED", response.ErrorMessage);
        Assert.False(_arm.Called("PUT", "/deploymentStacks/ads-stack"));
        Assert.Single(_db.AdsPlanApprovals);
    }

    [Fact]
    public async Task CreateStack_WithoutAToken_IsBlocked()
    {
        var preview = await Service.StackWhatIf(PreviewRequest(), Me);
        await Approve(preview.PlanHash);
        var request = CreateRequest(preview);
        request.WhatIfToken = "";

        var response = await Service.CreateStack(request, Me);

        Assert.Equal(400, response.StatusCode);
        Assert.StartsWith("WHAT_IF_REQUIRED", response.ErrorMessage);
    }

    [Fact]
    public async Task StackRpcsNeedAnIdentity()
    {
        Assert.Equal(401, (await Service.StackWhatIf(PreviewRequest(), TestServerCallContext.WithoutIdentity())).StatusCode);
        Assert.Empty(_arm.Calls);
    }
}

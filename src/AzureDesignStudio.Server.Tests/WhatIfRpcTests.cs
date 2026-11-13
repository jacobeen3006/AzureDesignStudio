using System.Net;
using System.Text.Json;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Discovery.Preflight;
using AzureDesignStudio.Services;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.SharedModels.Protos;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureDesignStudio.Server.Tests;

/// <summary>The WhatIf RPC against scripted ARM. The resource-group lookup and the what-if POST
/// both ride the "arm" HttpClient the service takes from IHttpClientFactory; the POST answers
/// 200 with the finished result, so no test polls.</summary>
public class WhatIfRpcTests
{
    private const string Template =
        """{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","resources":[{"type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","name":"sa1","location":"westeurope","sku":{"name":"Standard_LRS"},"kind":"StorageV2"}]}""";

    private const string ResourceGroup = "rg";
    private readonly DesignDbContext _db = TestData.NewDb();
    private readonly WhatIfTokenGate _gate = new(WhatIfTokenGate.DefaultTtl);
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _subscription = Guid.NewGuid();
    private readonly RouteTableHttp _arm = new();

    public WhatIfRpcTests()
    {
        _arm.Answer(HttpMethod.Get, SubscriptionPath, HttpStatusCode.OK,
                "{\"id\":\"" + SubscriptionPath + "\",\"subscriptionId\":\"" + _subscription + "\",\"displayName\":\"test\",\"state\":\"Enabled\"}")
            .Answer(HttpMethod.Get, GroupPath, HttpStatusCode.OK,
                "{\"id\":\"" + GroupPath + "\",\"name\":\"" + ResourceGroup + "\",\"location\":\"westeurope\",\"properties\":{\"provisioningState\":\"Succeeded\"}}")
            .Answer(HttpMethod.Post, WhatIfPath, HttpStatusCode.OK, WhatIfResult(StorageCreate("sa1")));
    }

    private Grpc.Core.ServerCallContext Me => TestServerCallContext.ForUser(_user, "Ada");

    private DeployService Service(IAmbientAzureCredential? ambient) =>
        new(_db, NullLogger<DeployService>.Instance, new FakeCryptoService(), _gate, _arm, ambient);

    private string SubscriptionPath => $"/subscriptions/{_subscription}";
    private string GroupPath => $"{SubscriptionPath}/resourceGroups/{ResourceGroup}";
    private string WhatIfPath => $"{GroupPath}/providers/Microsoft.Resources/deployments/{WhatIfClient.DeploymentName}/whatIf";

    private string StorageId(string name) => $"{GroupPath}/providers/Microsoft.Storage/storageAccounts/{name}";

    /// <summary>A Create row as ARM's WhatIfOperationResult carries it: the full resolved payload in <c>after</c>.</summary>
    private string StorageCreate(string name) =>
        "{\"resourceId\":\"" + StorageId(name) + "\",\"changeType\":\"Create\",\"after\":{\"id\":\"" + StorageId(name)
        + "\",\"name\":\"" + name + "\",\"type\":\"Microsoft.Storage/storageAccounts\",\"apiVersion\":\"2023-01-01\","
        + "\"location\":\"westeurope\",\"sku\":{\"name\":\"Standard_LRS\"},\"kind\":\"StorageV2\"}}";

    private static string WhatIfResult(params string[] changes) =>
        """{"status":"Succeeded","properties":{"changes":[""" + string.Join(",", changes) + "]}}";

    private DeploymentRequest Request() => new()
    {
        SubscriptionId = _subscription.ToString(),
        ResourceGroupName = ResourceGroup,
        ArmTemplate = Template,
        Parameters = "{}",
    };

    [Fact]
    public async Task WhatIf_ResourceGroupTarget_ReturnsTokenAndChanges()
    {
        var response = await Service(new AmbientCredential()).WhatIf(Request(), Me);

        Assert.True(response.StatusCode == 200, response.ErrorMessage);
        Assert.NotEqual("", response.WhatIfToken);
        Assert.StartsWith("sha256:", response.PlanHash);
        Assert.Equal("Succeeded", response.Status);
        Assert.Equal((1, 0, 0), (response.Creates, response.Modifies, response.Deletes));
        using var rows = JsonDocument.Parse(response.ChangesJson);
        var row = Assert.Single(rows.RootElement.EnumerateArray());
        Assert.Equal("Create", row.GetProperty("changeType").GetString());
        Assert.Equal(StorageId("sa1"), row.GetProperty("resourceId").GetString());

        // The group's region came from the scripted lookup, and the what-if went to the group.
        Assert.Single(_arm.To(HttpMethod.Get, GroupPath));
        var post = Assert.Single(_arm.To(HttpMethod.Post, WhatIfPath));
        Assert.Contains("api-version=2025-04-01", post.Query);
        using var body = JsonDocument.Parse(post.Body);
        Assert.False(body.RootElement.TryGetProperty("location", out _)); // ARM rejects one at resource-group scope
        var properties = body.RootElement.GetProperty("properties");
        Assert.Equal("Incremental", properties.GetProperty("mode").GetString());
        Assert.Equal("sa1", properties.GetProperty("template").GetProperty("resources")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task WhatIf_UnknownResourceGroup_Answers404()
    {
        _arm.Answer(HttpMethod.Get, GroupPath, HttpStatusCode.NotFound,
            RouteTableHttp.ArmError("ResourceGroupNotFound", $"Resource group '{ResourceGroup}' could not be found."));

        var response = await Service(new AmbientCredential()).WhatIf(Request(), Me);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("Cannot find the resource group.", response.ErrorMessage);
        Assert.Equal("", response.WhatIfToken);
        Assert.Single(_arm.To(HttpMethod.Get, GroupPath));
        Assert.Empty(_arm.To(HttpMethod.Post, WhatIfPath));
    }

    [Fact]
    public async Task WhatIf_NoCredential_Answers404()
    {
        // No linked service principal for the subscription and no signed-in user on the host.
        var response = await Service(ambient: null).WhatIf(Request(), Me);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("No subscription is found.", response.ErrorMessage);
        Assert.Equal("", response.WhatIfToken);
        Assert.Empty(_arm.Requests);
    }

    // ---------- Azure Policy preflight (Wave B2) ----------

    private string PolicyPath => $"{GroupPath}/providers/Microsoft.PolicyInsights/checkPolicyRestrictions";

    private string AllowedLocationsAssignment =>
        $"{SubscriptionPath}/providers/Microsoft.Authorization/policyAssignments/allowed-locations";

    private const string AllowedLocationsDefinition =
        "/providers/Microsoft.Authorization/policyDefinitions/e56962a6-4747-49cd-b67b-bf8b01975c4c";

    /// <summary>A checkPolicyRestrictions answer with no evaluations: nothing to report.</summary>
    private const string NoPolicyFindings = """{"fieldRestrictions":[],"contentEvaluationResult":{"policyEvaluations":[]}}""";

    /// <summary>A checkPolicyRestrictions answer in the documented shape: one NonCompliant Deny evaluation.</summary>
    private string DenyAnswer => JsonSerializer.Serialize(new
    {
        fieldRestrictions = Array.Empty<object>(),
        contentEvaluationResult = new
        {
            policyEvaluations = new[]
            {
                new
                {
                    policyInfo = new { policyDefinitionId = AllowedLocationsDefinition, policyAssignmentId = AllowedLocationsAssignment },
                    evaluationResult = "NonCompliant",
                    evaluationDetails = new
                    {
                        evaluatedExpressions = new[]
                        {
                            new
                            {
                                result = "False", expressionKind = "Field", expression = "location", path = "location",
                                expressionValue = "westeurope", targetValue = new[] { "northeurope" }, @operator = "In",
                            },
                        },
                        reason = "Location 'westeurope' is not allowed.",
                    },
                    effectDetails = new { policyEffect = "Deny" },
                },
            },
        },
    });

    private IReadOnlyList<RecordedRequest> PolicyRequests =>
        [.. _arm.Requests.Where(r => r.Path.Contains("checkPolicyRestrictions", StringComparison.OrdinalIgnoreCase))];

    [Fact]
    public async Task WhatIf_PolicyDeny_IsReturned_AndTokenStillIssued()
    {
        _arm.Answer(HttpMethod.Post, PolicyPath, HttpStatusCode.OK, DenyAnswer);

        var response = await Service(new AmbientCredential()).WhatIf(Request(), Me);

        Assert.True(response.StatusCode == 200, response.ErrorMessage);
        Assert.NotEqual("", response.WhatIfToken);
        Assert.False(response.GuardrailBlocked);
        Assert.True(_gate.ValidateAndConsume(response.WhatIfToken, _subscription.ToString(), ResourceGroup, Template, "{}").IsOk);

        var flag = Assert.Single(response.PolicyFlags);
        Assert.Equal(StorageId("sa1"), flag.ResourceId);
        Assert.Equal("Deny", flag.Verdict);
        Assert.Equal("Deny", flag.Effect);
        Assert.Equal(AllowedLocationsAssignment, flag.PolicyAssignmentId);
        Assert.Equal(AllowedLocationsDefinition, flag.PolicyDefinitionId);
        Assert.Equal("", flag.PolicyDefinitionReferenceId);
        Assert.Equal("Location 'westeurope' is not allowed.", flag.Reason);
        // The engine had nothing to say; its caveats follow (here: alone).
        Assert.Equal(PolicyPreflight.Caveats, response.PolicyNotes);

        var post = Assert.Single(PolicyRequests);
        Assert.Equal(PolicyPath, post.Path, ignoreCase: true);
        Assert.Contains("api-version=2024-10-01", post.Query);
        using var body = JsonDocument.Parse(post.Body);
        var details = body.RootElement.GetProperty("resourceDetails");
        Assert.Equal("Microsoft.Storage/storageAccounts", details.GetProperty("resourceContent").GetProperty("type").GetString());
        Assert.Equal("2023-01-01", details.GetProperty("apiVersion").GetString());
        Assert.True(body.RootElement.GetProperty("includeAuditEffect").GetBoolean());
    }

    [Fact]
    public async Task WhatIf_PolicyForbidden_AddsNote_TokenUnchanged()
    {
        var service = Service(new AmbientCredential());
        _arm.Answer(HttpMethod.Post, PolicyPath, HttpStatusCode.OK, NoPolicyFindings);
        var baseline = await service.WhatIf(Request(), Me);
        _arm.Answer(HttpMethod.Post, PolicyPath, HttpStatusCode.Forbidden, RouteTableHttp.ArmError("AuthorizationFailed",
            "The client does not have authorization to perform action 'Microsoft.PolicyInsights/checkPolicyRestrictions/read'."));

        var response = await service.WhatIf(Request(), Me);

        // The review is exactly what it is without the preflight; only the notes say what was not checked.
        Assert.True(response.StatusCode == 200, response.ErrorMessage);
        Assert.NotEqual("", response.WhatIfToken);
        Assert.True(_gate.ValidateAndConsume(response.WhatIfToken, _subscription.ToString(), ResourceGroup, Template, "{}").IsOk);
        Assert.Equal((baseline.Status, baseline.PlanHash, baseline.Caveats, baseline.ChangesJson, baseline.GuardrailBlocked),
            (response.Status, response.PlanHash, response.Caveats, response.ChangesJson, response.GuardrailBlocked));
        Assert.Equal((1, 0, 0), (response.Creates, response.Modifies, response.Deletes));
        Assert.Equal("", response.ErrorMessage);

        Assert.Empty(response.PolicyFlags);
        Assert.Equal(
            ["No permission to check Azure Policy (needs Microsoft.PolicyInsights/checkPolicyRestrictions/read).", .. PolicyPreflight.Caveats],
            response.PolicyNotes);
    }

    [Fact]
    public async Task WhatIf_WhatIfFails_NoPolicyCall()
    {
        _arm.Answer(HttpMethod.Post, PolicyPath, HttpStatusCode.OK, DenyAnswer)
            .Answer(HttpMethod.Post, WhatIfPath, HttpStatusCode.BadRequest,
                RouteTableHttp.ArmError("InvalidTemplateDeployment", "The template deployment 'ads-deployment' is not valid."));

        var response = await Service(new AmbientCredential()).WhatIf(Request(), Me);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("", response.WhatIfToken);
        Assert.Single(_arm.To(HttpMethod.Post, WhatIfPath));
        Assert.Empty(PolicyRequests);
        Assert.Empty(response.PolicyFlags);
        Assert.Empty(response.PolicyNotes);
    }

    [Fact]
    public async Task WhatIf_ManagementGroupTarget_SkipsPolicyWithNote()
    {
        const string mgTemplate =
            """{"$schema":"https://schema.management.azure.com/schemas/2019-08-01/managementGroupDeploymentTemplate.json#","contentVersion":"1.0.0.0","resources":[{"type":"Microsoft.Authorization/roleAssignments","apiVersion":"2022-04-01","name":"ra1","properties":{"roleDefinitionId":"/providers/Microsoft.Authorization/roleDefinitions/acdd72a7-3385-48ef-bd42-f606fba81ae7","principalId":"00000000-0000-0000-0000-000000000001"}}]}""";
        const string mgWhatIfPath =
            "/providers/Microsoft.Management/managementGroups/mg1/providers/Microsoft.Resources/deployments/ads-deployment/whatIf";
        // A row under a subscription that the engine would otherwise check.
        _arm.Answer(HttpMethod.Post, mgWhatIfPath, HttpStatusCode.OK, WhatIfResult(StorageCreate("sa1")))
            .Answer(HttpMethod.Post, PolicyPath, HttpStatusCode.OK, DenyAnswer);
        var request = new DeploymentRequest
        {
            SubscriptionId = _subscription.ToString(),
            ArmTemplate = mgTemplate,
            Parameters = "{}",
            Target = DeployTarget.ManagementGroup,
            ManagementGroupId = "mg1",
            Location = "westeurope",
        };

        var response = await Service(new AmbientCredential()).WhatIf(request, Me);

        Assert.True(response.StatusCode == 200, response.ErrorMessage);
        Assert.NotEqual("", response.WhatIfToken);
        Assert.Single(_arm.To(HttpMethod.Post, mgWhatIfPath));
        Assert.Empty(PolicyRequests);
        Assert.Empty(response.PolicyFlags);
        Assert.Equal(["Policy is not checked for management-group or tenant deployments."], response.PolicyNotes);
    }

    [Fact]
    public async Task WhatIf_PolicyRequestBody_HasNoSecrets()
    {
        const string secret = "S3cretPa55word";
        const string vmTemplate =
            """{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","parameters":{"adminPassword":{"type":"secureString"}},"resources":[{"type":"Microsoft.Compute/virtualMachines","apiVersion":"2024-07-01","name":"vm1","location":"westeurope","properties":{"hardwareProfile":{"vmSize":"Standard_D2s_v5"},"osProfile":{"computerName":"vm1","adminUsername":"azureuser","adminPassword":"[parameters('adminPassword')]","linuxConfiguration":{"disablePasswordAuthentication":false}}}}]}""";
        var vmId = $"{GroupPath}/providers/Microsoft.Compute/virtualMachines/vm1";
        // Whether what-if masks secure values in after payloads is not documented: assume it does not,
        // and let the value turn up elsewhere too.
        var vmCreate = "{\"resourceId\":\"" + vmId + "\",\"changeType\":\"Create\",\"after\":{\"id\":\"" + vmId
            + "\",\"name\":\"vm1\",\"type\":\"Microsoft.Compute/virtualMachines\",\"apiVersion\":\"2024-07-01\",\"location\":\"westeurope\","
            + "\"tags\":{\"note\":\"first login with " + secret + " then rotate\"},"
            + "\"properties\":{\"hardwareProfile\":{\"vmSize\":\"Standard_D2s_v5\"},\"osProfile\":{\"computerName\":\"vm1\","
            + "\"adminUsername\":\"azureuser\",\"adminPassword\":\"" + secret + "\",\"linuxConfiguration\":{\"disablePasswordAuthentication\":false}}}}}";
        _arm.Answer(HttpMethod.Post, WhatIfPath, HttpStatusCode.OK, WhatIfResult(vmCreate))
            .Answer(HttpMethod.Post, PolicyPath, HttpStatusCode.OK, NoPolicyFindings);
        var request = Request();
        request.ArmTemplate = vmTemplate;
        request.Parameters = "{\"adminPassword\":{\"value\":\"" + secret + "\"}}";

        var response = await Service(new AmbientCredential()).WhatIf(request, Me);

        Assert.True(response.StatusCode == 200, response.ErrorMessage);
        Assert.NotEqual("", response.WhatIfToken);
        var post = Assert.Single(PolicyRequests);
        Assert.DoesNotContain(secret, post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("adminPassword", post.Body, StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(post.Body);
        var content = body.RootElement.GetProperty("resourceDetails").GetProperty("resourceContent");
        var osProfile = content.GetProperty("properties").GetProperty("osProfile");
        Assert.False(osProfile.GetProperty("linuxConfiguration").GetProperty("disablePasswordAuthentication").GetBoolean());
        Assert.Equal("azureuser", osProfile.GetProperty("adminUsername").GetString());
        Assert.Equal("Standard_D2s_v5", content.GetProperty("properties").GetProperty("hardwareProfile").GetProperty("vmSize").GetString());
        Assert.False(content.GetProperty("tags").TryGetProperty("note", out _));
        // The deployment parameters themselves are never sent.
        Assert.False(body.RootElement.TryGetProperty("parameters", out _));
        Assert.False(body.RootElement.GetProperty("resourceDetails").TryGetProperty("parameters", out _));
    }

    [Fact]
    public async Task WhatIf_PolicyCheckHangs_StopsAtTheBudget_TokenStillIssued()
    {
        _arm.Hold(HttpMethod.Post, PolicyPath, new TaskCompletionSource().Task);
        var service = Service(new AmbientCredential());
        service.PreflightBudget = TimeSpan.FromMilliseconds(200);

        var response = await service.WhatIf(Request(), Me);

        Assert.True(response.StatusCode == 200, response.ErrorMessage);
        Assert.NotEqual("", response.WhatIfToken);
        Assert.Single(PolicyRequests);
        Assert.Empty(response.PolicyFlags);
        Assert.Equal(["Policy check stopped at the time limit: 1 of 1 resources not checked.", .. PolicyPreflight.Caveats],
            response.PolicyNotes);
    }
}

/// <summary>The route table itself: what the WhatIf tests and the preflight tests lean on.</summary>
public class RouteTableHttpTests
{
    [Fact]
    public async Task HeldRoute_EndsWhenTheRequestIsCancelled_AndEveryRequestIsRecorded()
    {
        var routes = new RouteTableHttp().Hold(HttpMethod.Post, "/slow", new TaskCompletionSource().Task);
        using var http = routes.CreateClient("arm");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            http.PostAsync("https://management.azure.com/SLOW?api-version=1", new StringContent("{\"a\":1}"), cancel.Token));
        var unrouted = await http.GetAsync("https://management.azure.com/elsewhere");

        Assert.Equal(HttpStatusCode.NotImplemented, unrouted.StatusCode);
        RecordedRequest[] expected =
            [new("POST", "/SLOW", "?api-version=1", "{\"a\":1}"), new("GET", "/elsewhere", "", "")];
        Assert.Equal(expected, routes.Requests);
    }
}

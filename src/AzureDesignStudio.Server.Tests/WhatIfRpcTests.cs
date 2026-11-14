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
        Assert.Empty(QuotaRequests);
        Assert.Empty(response.QuotaFlags);
        Assert.Empty(response.QuotaNotes);
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
        Assert.Empty(QuotaRequests);
        Assert.Empty(response.QuotaFlags);
        Assert.Equal(["Quota is not checked for management-group or tenant deployments."], response.QuotaNotes);
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
        // Wide enough that the first POST is always sent before the deadline, even with the policy
        // path cold and the quota reads running beside it; the held route still ends the test at
        // the budget.
        service.PreflightBudget = TimeSpan.FromSeconds(2);

        var response = await service.WhatIf(Request(), Me);

        Assert.True(response.StatusCode == 200, response.ErrorMessage);
        Assert.NotEqual("", response.WhatIfToken);
        Assert.Single(PolicyRequests);
        Assert.Empty(response.PolicyFlags);
        Assert.Equal(["Policy check stopped at the time limit: 1 of 1 resources not checked.", .. PolicyPreflight.Caveats],
            response.PolicyNotes);
    }

    // ---------- quota preflight (Wave B4) ----------

    private const string DSv5 = "standardDSv5Family";

    private string SkusPath => $"{SubscriptionPath}/providers/Microsoft.Compute/skus";
    private string ComputeUsagesPath => $"{SubscriptionPath}/providers/Microsoft.Compute/locations/westeurope/usages";

    private IReadOnlyList<RecordedRequest> QuotaRequests =>
        [.. _arm.Requests.Where(r => r.Path.EndsWith("/skus", StringComparison.OrdinalIgnoreCase)
            || r.Path.EndsWith("/usages", StringComparison.OrdinalIgnoreCase))];

    private string VmId(string name) => $"{GroupPath}/providers/Microsoft.Compute/virtualMachines/{name}";

    /// <summary>vm1..vmN of one size. The what-if is scripted; the template only has to pass the
    /// validator and the token gate.</summary>
    private static string VmTemplate(int count, string size) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["$schema"] = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
        ["contentVersion"] = "1.0.0.0",
        ["resources"] = Enumerable.Range(1, count).Select(i => new
        {
            type = "Microsoft.Compute/virtualMachines",
            apiVersion = "2024-07-01",
            name = $"vm{i}",
            location = "westeurope",
            properties = new { hardwareProfile = new { vmSize = size } },
        }).ToArray(),
    });

    /// <summary>The what-if Create rows for <see cref="VmTemplate"/>: the resolved payload in <c>after</c>.</summary>
    private string[] VmCreates(int count, string size) =>
    [
        .. Enumerable.Range(1, count).Select(i => JsonSerializer.Serialize(new
        {
            resourceId = VmId($"vm{i}"),
            changeType = "Create",
            after = new
            {
                id = VmId($"vm{i}"),
                name = $"vm{i}",
                type = "Microsoft.Compute/virtualMachines",
                apiVersion = "2024-07-01",
                location = "westeurope",
                properties = new
                {
                    hardwareProfile = new { vmSize = size },
                    osProfile = new { computerName = $"vm{i}", adminUsername = "azureuser" },
                },
            },
        })),
    ];

    private DeploymentRequest VmRequest(int count, string size)
    {
        _arm.Answer(HttpMethod.Post, WhatIfPath, HttpStatusCode.OK, WhatIfResult(VmCreates(count, size)));
        var request = Request();
        request.ArmTemplate = VmTemplate(count, size);
        return request;
    }

    private static object Sku(string name, int vCpus) => new
    {
        resourceType = "virtualMachines",
        name,
        tier = "Standard",
        size = name["Standard_".Length..],
        family = DSv5,
        locations = new[] { "westeurope" },
        capabilities = new[] { new { name = "vCPUs", value = vCpus.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
        restrictions = Array.Empty<object>(),
    };

    /// <summary>The Resource SKUs list for westeurope, in the documented shape.</summary>
    private static string SkuList => JsonSerializer.Serialize(new { value = new[] { Sku("Standard_D2s_v5", 2), Sku("Standard_D4s_v5", 4) } });

    private static object Usage(string name, string localizedValue, long current, long limit) =>
        new { name = new { value = name, localizedValue }, currentValue = current, limit, unit = "Count" };

    /// <summary>The compute usages for westeurope: the regional total at 10 of 100, DSv5 as given.</summary>
    private static string ComputeUsages(long familyCurrent, long familyLimit = 50) => JsonSerializer.Serialize(new
    {
        value = new[]
        {
            Usage("cores", "Total Regional vCPUs", 10, 100),
            Usage(DSv5, "Standard DSv5 Family vCPUs", familyCurrent, familyLimit),
        },
    });

    [Fact]
    public async Task WhatIf_QuotaExceeds_IsReturned_TokenStillIssued()
    {
        var request = VmRequest(2, "Standard_D4s_v5");
        _arm.Answer(HttpMethod.Post, PolicyPath, HttpStatusCode.OK, NoPolicyFindings)
            .Answer(HttpMethod.Get, SkusPath, HttpStatusCode.OK, SkuList)
            .Answer(HttpMethod.Get, ComputeUsagesPath, HttpStatusCode.OK, ComputeUsages(familyCurrent: 44));

        var response = await Service(new AmbientCredential()).WhatIf(request, Me);

        Assert.True(response.StatusCode == 200, response.ErrorMessage);
        Assert.NotEqual("", response.WhatIfToken);
        Assert.False(response.GuardrailBlocked);
        Assert.True(_gate.ValidateAndConsume(response.WhatIfToken, _subscription.ToString(), ResourceGroup, request.ArmTemplate, "{}").IsOk);
        Assert.Equal((2, 0, 0), (response.Creates, response.Modifies, response.Deletes));

        // Two D4s_v5 ask 8 vCPUs: 44 + 8 > 50 for the family; 10 + 8 of 100 regionally.
        QuotaFlag[] expected =
        [
            new()
            {
                Location = "westeurope", QuotaName = DSv5, DisplayName = "Standard DSv5 Family vCPUs",
                Current = 44, Limit = 50, Requested = 8, Status = "Exceeds", VmSize = "Standard_D4s_v5", Detail = "",
            },
            new()
            {
                Location = "westeurope", QuotaName = "cores", DisplayName = "Total Regional vCPUs",
                Current = 10, Limit = 100, Requested = 8, Status = "Ok", VmSize = "", Detail = "",
            },
        ];
        Assert.Equal(expected, response.QuotaFlags);
        Assert.Equal(QuotaPreflight.Caveats, response.QuotaNotes);

        // The reads went to the target's subscription, once each; no public IP, so no network read.
        var skus = Assert.Single(_arm.To(HttpMethod.Get, SkusPath));
        Assert.Contains("api-version=2021-07-01", skus.Query);
        Assert.Contains("$filter=location eq 'westeurope'", Uri.UnescapeDataString(skus.Query));
        var usages = Assert.Single(_arm.To(HttpMethod.Get, ComputeUsagesPath));
        Assert.Contains("api-version=2026-04-01", usages.Query);
        Assert.Equal(2, QuotaRequests.Count);
    }

    [Fact]
    public async Task WhatIf_PolicyAndQuotaBothReturned()
    {
        var request = VmRequest(1, "Standard_D4s_v5");
        // Each preflight answers only once the other has started: run one after the other, the
        // first would wait out the budget and come back empty.
        var policyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quotaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _arm.Answer(HttpMethod.Post, PolicyPath, async (_, token) =>
            {
                policyStarted.TrySetResult();
                await quotaStarted.Task.WaitAsync(token);
                return new ScriptedAnswer(HttpStatusCode.OK, DenyAnswer);
            })
            .Answer(HttpMethod.Get, SkusPath, async (_, token) =>
            {
                quotaStarted.TrySetResult();
                await policyStarted.Task.WaitAsync(token);
                return new ScriptedAnswer(HttpStatusCode.OK, SkuList);
            })
            .Answer(HttpMethod.Get, ComputeUsagesPath, HttpStatusCode.OK, ComputeUsages(familyCurrent: 44));
        var service = Service(new AmbientCredential());
        service.PreflightBudget = TimeSpan.FromSeconds(10);

        var response = await service.WhatIf(request, Me);

        Assert.True(response.StatusCode == 200, response.ErrorMessage);
        Assert.NotEqual("", response.WhatIfToken);
        Assert.True(_gate.ValidateAndConsume(response.WhatIfToken, _subscription.ToString(), ResourceGroup, request.ArmTemplate, "{}").IsOk);

        var policy = Assert.Single(response.PolicyFlags);
        Assert.Equal((VmId("vm1"), "Deny"), (policy.ResourceId, policy.Verdict));
        Assert.Equal(PolicyPreflight.Caveats, response.PolicyNotes);

        // One D4s_v5: 44 + 4 of 50 is at least 90 % of the family's limit.
        Assert.Equal(
            [("westeurope", DSv5, "NearLimit", 44L, 50L, 4L), ("westeurope", "cores", "Ok", 10L, 100L, 4L)],
            response.QuotaFlags.Select(f => (f.Location, f.QuotaName, f.Status, f.Current, f.Limit, f.Requested)));
        Assert.Equal(QuotaPreflight.Caveats, response.QuotaNotes);
    }

    [Fact]
    public async Task WhatIf_PreflightBudget_KeepsPartialResults()
    {
        var request = VmRequest(6, "Standard_D2s_v5");
        // Policy: vm1..vm5 answer Deny, vm6 never answers. Quota: the SKUs answer, the compute usages never do.
        _arm.Answer(HttpMethod.Post, PolicyPath, async (sent, token) =>
            {
                using var body = JsonDocument.Parse(sent.Body);
                var name = body.RootElement.GetProperty("resourceDetails").GetProperty("resourceContent").GetProperty("name").GetString();
                if (name == "vm6")
                    await Task.Delay(Timeout.Infinite, token); // only the deadline ends this one
                return new ScriptedAnswer(HttpStatusCode.OK, DenyAnswer);
            })
            .Answer(HttpMethod.Get, SkusPath, HttpStatusCode.OK, SkuList)
            .Hold(HttpMethod.Get, ComputeUsagesPath, new TaskCompletionSource().Task);
        var service = Service(new AmbientCredential());
        service.PreflightBudget = TimeSpan.FromSeconds(1);

        var response = await service.WhatIf(request, Me);

        Assert.True(response.StatusCode == 200, response.ErrorMessage);
        Assert.NotEqual("", response.WhatIfToken);
        Assert.True(_gate.ValidateAndConsume(response.WhatIfToken, _subscription.ToString(), ResourceGroup, request.ArmTemplate, "{}").IsOk);
        Assert.Equal((6, 0, 0), (response.Creates, response.Modifies, response.Deletes));

        // The five that answered are kept; the sixth is a note, not a silent "no findings".
        Assert.Equal(
            [.. Enumerable.Range(1, 5).Select(i => (VmId($"vm{i}"), "Deny"))],
            response.PolicyFlags.Select(f => (f.ResourceId, f.Verdict)));
        Assert.Equal(["Policy check stopped at the time limit: 1 of 6 resources not checked.", .. PolicyPreflight.Caveats],
            response.PolicyNotes);

        // Six D2s_v5 ask 12 vCPUs; without the usages neither check can say Ok.
        const string late = "the compute usages were not read within the time limit";
        QuotaFlag[] expected =
        [
            new()
            {
                Location = "westeurope", QuotaName = "cores", DisplayName = "cores",
                Current = 0, Limit = 0, Requested = 12, Status = "Unknown", VmSize = "", Detail = late,
            },
            new()
            {
                Location = "westeurope", QuotaName = DSv5, DisplayName = DSv5,
                Current = 0, Limit = 0, Requested = 12, Status = "Unknown", VmSize = "Standard_D2s_v5", Detail = late,
            },
        ];
        Assert.Equal(expected, response.QuotaFlags);
        Assert.Equal(["Quota check stopped at the time limit: 1 of 2 quota reads did not finish.", .. QuotaPreflight.Caveats],
            response.QuotaNotes);
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

    [Fact]
    public async Task ARequestCancelledBeforeItArrives_IsStillRecorded()
    {
        // HttpClient hands the handler a request whose token has already fired, and the handler
        // reads its in-memory body without observing that token, so a request that reaches the
        // handler is always recorded before the hold throws. A request the deadline stops before
        // it is sent - the engine starts none after its deadline, and ArmTransport waits on its
        // gate and the credential with the same token before http.SendAsync - never reaches the
        // harness, so a deadline test that counts requests still needs a budget wide enough for
        // the first send. If this test ever fails, read the body with CancellationToken.None in
        // RouteTableHttp.SendAsync.
        var routes = new RouteTableHttp().Hold(HttpMethod.Post, "/slow", new TaskCompletionSource().Task);
        using var http = routes.CreateClient("arm");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            http.PostAsync("https://management.azure.com/slow", new StringContent("{\"a\":1}"), cancel.Token));

        Assert.Equal([new RecordedRequest("POST", "/slow", "", "{\"a\":1}")], routes.Requests);
    }
}

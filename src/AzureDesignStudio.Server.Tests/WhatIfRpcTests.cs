using System.Net;
using System.Text.Json;
using AzureDesignStudio.Discovery.Iac.WhatIf;
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

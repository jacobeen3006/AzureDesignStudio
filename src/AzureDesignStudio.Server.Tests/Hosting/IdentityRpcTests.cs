using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Grpc.AspNetCore.Server;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>
/// The typed caller over the real host: a bearer-style principal with an object id and a tenant
/// id reaches the service as one caller; a missing or malformed one answers 401 in the response,
/// never as an RpcException (the client reads Unauthenticated as "offline, retry later").
/// Tracer on Design.GetSaved; plan 05-02 widens it to every Design and Deploy RPC.
/// </summary>
public sealed class IdentityRpcTests(AdsServerFactory factory) : IClassFixture<AdsServerFactory>
{
    private async Task<int> GetSavedStatus(TestClaims? claims)
    {
        using var channel = factory.CreateGrpcChannel(Guid.NewGuid(), claims: claims);
        return (await new Design.DesignClient(channel).GetSavedAsync(new Empty())).StatusCode;
    }

    [Fact]
    public async Task ATokenWithOidAndTidIsACaller() =>
        Assert.Equal(200, await GetSavedStatus(null));

    [Fact]
    public async Task ATokenWithTheShortSpellingsIsTheSameCaller() =>
        Assert.Equal(200, await GetSavedStatus(new TestClaims(
            NoOid: true, NoTid: true,
            Extra: [("oid", Guid.NewGuid().ToString()), ("tid", TestTenants.Fabrikam.ToString())])));

    [Fact]
    public async Task ATokenWithoutTidAnswers401InTheResponse() =>
        Assert.Equal(401, await GetSavedStatus(new TestClaims(NoTid: true)));

    [Fact]
    public async Task ATokenWithoutOidAnswers401InTheResponse() =>
        Assert.Equal(401, await GetSavedStatus(new TestClaims(NoOid: true)));

    [Fact]
    public async Task ATokenWithAnEmptyTenantAnswers401InTheResponse() =>
        Assert.Equal(401, await GetSavedStatus(new TestClaims(Tenant: Guid.Empty)));

    [Fact]
    public async Task ATokenWithANonGuidTenantAnswers401InTheResponse() =>
        Assert.Equal(401, await GetSavedStatus(new TestClaims(
            NoTid: true, Extra: [("tid", "not-a-guid")])));

    // --- every Design and Deploy RPC ---

    /// <summary>Each RPC by name, called with an empty request; the answer is the response's StatusCode.</summary>
    private static readonly Dictionary<string, Func<GrpcChannel, Task<int>>> Rpcs = new()
    {
        ["Design.Save"] = async c => (await new Design.DesignClient(c).SaveAsync(new SaveDesignRequest())).StatusCode,
        ["Design.GetSaved"] = async c => (await new Design.DesignClient(c).GetSavedAsync(new Empty())).StatusCode,
        ["Design.Load"] = async c => (await new Design.DesignClient(c).LoadAsync(new LoadDesignRequest())).StatusCode,
        ["Design.Delete"] = async c => (await new Design.DesignClient(c).DeleteAsync(new DeleteDesignRequest())).StatusCode,
        ["Deploy.SaveSubscriptionInfo"] = async c => (await new Deploy.DeployClient(c).SaveSubscriptionInfoAsync(new SubscriptionInfo())).StatusCode,
        ["Deploy.LoadSubscriptionInfo"] = async c => (await new Deploy.DeployClient(c).LoadSubscriptionInfoAsync(new Empty())).StatusCode,
        ["Deploy.GetResourceGroups"] = async c => (await new Deploy.DeployClient(c).GetResourceGroupsAsync(new GetRgsRequest())).StatusCode,
        ["Deploy.WhatIf"] = async c => (await new Deploy.DeployClient(c).WhatIfAsync(new DeploymentRequest())).StatusCode,
        ["Deploy.ApprovePlan"] = async c => (await new Deploy.DeployClient(c).ApprovePlanAsync(new PlanApprovalRequest())).StatusCode,
        ["Deploy.CreateDeployment"] = async c =>
        {
            // Server-streaming: the 401 is the first (and only) streamed message.
            using var call = new Deploy.DeployClient(c).CreateDeployment(new DeploymentRequest());
            Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
            return call.ResponseStream.Current.StatusCode;
        },
        ["Deploy.GetDeploymentOperations"] = async c => (await new Deploy.DeployClient(c).GetDeploymentOperationsAsync(new DeploymentOperationsRequest())).StatusCode,
        ["Deploy.PrepareRetry"] = async c => (await new Deploy.DeployClient(c).PrepareRetryAsync(new PrepareRetryRequest())).StatusCode,
        ["Deploy.CreateStack"] = async c => (await new Deploy.DeployClient(c).CreateStackAsync(new CreateStackRequest())).StatusCode,
        ["Deploy.StackWhatIf"] = async c => (await new Deploy.DeployClient(c).StackWhatIfAsync(new StackWhatIfRequest())).StatusCode,
        ["Deploy.GetStack"] = async c => (await new Deploy.DeployClient(c).GetStackAsync(new GetStackRequest())).StatusCode,
        ["Deploy.SetGuardrailPolicy"] = async c => (await new Deploy.DeployClient(c).SetGuardrailPolicyAsync(new GuardrailPolicyRequest())).StatusCode,
        ["Deploy.GetGuardrailPolicy"] = async c => (await new Deploy.DeployClient(c).GetGuardrailPolicyAsync(new Empty())).StatusCode,
        ["Deploy.ApproveDecomWindow"] = async c => (await new Deploy.DeployClient(c).ApproveDecomWindowAsync(new ApproveDecomWindowRequest())).StatusCode,
        ["Deploy.ExecuteDecomWindow"] = async c => (await new Deploy.DeployClient(c).ExecuteDecomWindowAsync(new ExecuteDecomWindowRequest())).StatusCode,
        ["Deploy.ReleaseDecomWindow"] = async c => (await new Deploy.DeployClient(c).ReleaseDecomWindowAsync(new ReleaseDecomWindowRequest())).StatusCode,
    };

    /// <summary>The one RPC that is not identity-bound: it reads the public retail price list.</summary>
    private const string PublicRpc = "Cost.GetRetailPrices";

    [Fact]
    public void TheTableListsTwentyRpcs() => Assert.Equal(20, Rpcs.Count);

    [Fact]
    public void TheTableCoversEveryGrpcMethodOfTheHost()
    {
        var hosted = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .Select(endpoint => endpoint.Metadata.GetMetadata<GrpcMethodMetadata>())
            .Where(metadata => metadata is not null)
            .Select(metadata => $"{metadata!.Method.ServiceName}.{metadata.Method.Name}")
            .Where(name => !name.StartsWith("grpc.", StringComparison.Ordinal)) // reflection and health, Debug only
            .ToHashSet();
        hosted.Remove(PublicRpc);

        Assert.Equal(hosted.Order(), Rpcs.Keys.Order());
    }

    public static TheoryData<string> RpcNames => new(Rpcs.Keys);

    private static readonly TestClaims NoTenant = new(NoTid: true);

    /// <summary>A short <c>oid</c> that is not a GUID, in place of the default object id claim.</summary>
    private static readonly TestClaims NonGuidShortOid = new(NoOid: true, Extra: [("oid", "not-a-guid")]);

    [Theory]
    [MemberData(nameof(RpcNames))]
    public async Task EveryRpcAnswers401InTheResponseForATokenWithoutTid(string rpc)
    {
        using var channel = factory.CreateGrpcChannel(Guid.NewGuid(), claims: NoTenant);

        Assert.Equal(401, await Rpcs[rpc](channel));
    }

    [Theory]
    [MemberData(nameof(RpcNames))]
    public async Task EveryRpcAnswers401InTheResponseForANonGuidShortOid(string rpc)
    {
        using var channel = factory.CreateGrpcChannel(Guid.NewGuid(), claims: NonGuidShortOid);

        Assert.Equal(401, await Rpcs[rpc](channel));
    }
}

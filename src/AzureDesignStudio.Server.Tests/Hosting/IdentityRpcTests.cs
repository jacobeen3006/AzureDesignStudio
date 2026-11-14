using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
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

    // --- every Design RPC (Task 2 grows this table to the Deploy RPCs) ---

    /// <summary>Each RPC by name, called with an empty request; the answer is the response's StatusCode.</summary>
    private static readonly Dictionary<string, Func<GrpcChannel, Task<int>>> Rpcs = new()
    {
        ["Design.Save"] = async c => (await new Design.DesignClient(c).SaveAsync(new SaveDesignRequest())).StatusCode,
        ["Design.GetSaved"] = async c => (await new Design.DesignClient(c).GetSavedAsync(new Empty())).StatusCode,
        ["Design.Load"] = async c => (await new Design.DesignClient(c).LoadAsync(new LoadDesignRequest())).StatusCode,
        ["Design.Delete"] = async c => (await new Design.DesignClient(c).DeleteAsync(new DeleteDesignRequest())).StatusCode,
    };

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

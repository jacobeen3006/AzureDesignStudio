using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
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
}

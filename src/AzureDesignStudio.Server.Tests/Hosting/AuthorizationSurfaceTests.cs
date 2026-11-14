using System.Net;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>
/// The host denies by default (a FallbackPolicy) and exposes exactly one anonymous gRPC method,
/// Cost.GetRetailPrices. The anonymous list is written as a literal in this file on purpose:
/// changing the production list must break the test.
/// </summary>
public sealed class AuthorizationSurfaceTests(AdsServerFactory factory) : IClassFixture<AdsServerFactory>
{
    [Fact]
    public void TheHostHasAnAuthenticatedUserFallbackPolicy()
    {
        var policy = factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy;

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task AnAnonymousDesignCallIsUnauthenticated()
    {
        using var channel = factory.CreateGrpcChannel();

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => new Design.DesignClient(channel).GetSavedAsync(new Empty()).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task AnAnonymousCostCallReachesTheService()
    {
        using var channel = factory.CreateGrpcChannel();

        var prices = await new Cost.CostClient(channel).GetRetailPricesAsync(new RetailPriceRequest());

        // An empty request is refused by the service itself, before any call to the price API.
        Assert.Equal(400, prices.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    public async Task TheSpaIsNotChallengedForAnAnonymousBrowser(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

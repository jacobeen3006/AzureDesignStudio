using AzureDesignStudio.Services;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>
/// Compatibility over the real host (IDA-02): a token that carries only the legacy object-id URI
/// claim (plus a tenant) reads, saves, deletes and links exactly the rows it did before, and the
/// same user presenting the short <c>oid</c> claim addresses the same rows and the same quota.
/// </summary>
public sealed class LegacyClaimTests(AdsServerFactory factory) : IClassFixture<AdsServerFactory>
{
    /// <summary>The URI object-id claim only (the default test token).</summary>
    private static readonly TestClaims LegacyUri = new();

    /// <summary>The short <c>oid</c> claim only, the spelling inbound claim mapping off produces.</summary>
    private static TestClaims ShortOid(Guid user) => new(NoOid: true, Extra: [("oid", user.ToString())]);

    [Fact]
    public async Task ALegacyUriOnlyTokenSavesListsLoadsAndDeletesDesigns()
    {
        var user = Guid.NewGuid();
        using var channel = factory.CreateGrpcChannel(user, claims: LegacyUri);
        var design = new Design.DesignClient(channel);

        Assert.Equal(200, (await design.SaveAsync(new SaveDesignRequest { Name = "legacy", Data = "{\"v\":1}" })).StatusCode);

        var saved = await design.GetSavedAsync(new Empty());
        Assert.Equal(200, saved.StatusCode);
        Assert.Equal(["legacy"], saved.Names);

        var loaded = await design.LoadAsync(new LoadDesignRequest { Name = "legacy" });
        Assert.Equal(200, loaded.StatusCode);
        Assert.Equal("{\"v\":1}", loaded.Data);

        Assert.Equal(200, (await design.DeleteAsync(new DeleteDesignRequest { Name = "legacy" })).StatusCode);
        Assert.Empty((await design.GetSavedAsync(new Empty())).Names);
    }

    [Fact]
    public async Task ALegacyUriOnlyTokenLinksASubscriptionAndReadsItBack()
    {
        using var withFake = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ICryptoService>(new FakeCryptoService())));
        var user = Guid.NewGuid();
        var request = new SubscriptionInfo
        {
            SubscriptionId = Guid.NewGuid().ToString(),
            TenantId = Guid.NewGuid().ToString(),
            ClientId = Guid.NewGuid().ToString(),
            ClientSecret = "secret",
            SubscriptionName = "legacy link",
        };

        using (var legacy = withFake.CreateGrpcChannel(user, claims: LegacyUri))
            Assert.Equal(200, (await new Deploy.DeployClient(legacy).SaveSubscriptionInfoAsync(request)).StatusCode);

        // The same user, presenting only the short spelling, sees the same link.
        using var shortSpelling = withFake.CreateGrpcChannel(user, claims: ShortOid(user));
        var loaded = await new Deploy.DeployClient(shortSpelling).LoadSubscriptionInfoAsync(new Empty());
        Assert.Equal(200, loaded.StatusCode);
        Assert.Equal(request.SubscriptionId, Assert.Single(loaded.SubscriptionData).SubscriptionId);
    }

    [Fact]
    public async Task BothSpellingsAddressTheSameDesignsAndTheSameQuota()
    {
        var user = Guid.NewGuid();
        using var legacy = factory.CreateGrpcChannel(user, claims: LegacyUri);
        using var shortSpelling = factory.CreateGrpcChannel(user, claims: ShortOid(user));
        var viaLegacy = new Design.DesignClient(legacy);
        var viaShort = new Design.DesignClient(shortSpelling);

        for (var i = 0; i < 10; i++)
            Assert.Equal(200, (await viaLegacy.SaveAsync(new SaveDesignRequest { Name = $"design {i}", Data = "{}" })).StatusCode);

        // The same rows ...
        var seen = await viaShort.GetSavedAsync(new Empty());
        Assert.Equal(10, seen.Names.Count);
        Assert.Equal(200, (await viaShort.LoadAsync(new LoadDesignRequest { Name = "design 3" })).StatusCode);

        // ... and the same quota: the eleventh is refused whichever spelling saved the first ten,
        // while replacing an existing design is still allowed.
        Assert.Equal(507, (await viaShort.SaveAsync(new SaveDesignRequest { Name = "one more", Data = "{}" })).StatusCode);
        Assert.Equal(507, (await viaLegacy.SaveAsync(new SaveDesignRequest { Name = "one more", Data = "{}" })).StatusCode);
        Assert.Equal(200, (await viaShort.SaveAsync(new SaveDesignRequest { Name = "design 0", Data = "{\"v\":2}" })).StatusCode);
    }
}

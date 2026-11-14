using AzureDesignStudio.Services;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using AzureDesignStudio.Services.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Hosting;

public class ServerHostTests(AdsServerFactory factory) : IClassFixture<AdsServerFactory>
{
    [Fact]
    public async Task BootsWithTheCheckedInConfigurationAndRoundTripsADesign()
    {
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        Assert.False(ServiceTools.IsConfigured(configuration["KeyVaultName"]));

        using var channel = factory.CreateGrpcChannel(Guid.NewGuid());
        var design = new Design.DesignClient(channel);

        var saved = await design.SaveAsync(new SaveDesignRequest { Name = "tracer design", Data = """{"nodes":[]}""" });
        Assert.Equal(200, saved.StatusCode);

        var list = await design.GetSavedAsync(new Empty());
        Assert.Equal(200, list.StatusCode);
        Assert.Contains("tracer design", list.Names);

        var loaded = await design.LoadAsync(new LoadDesignRequest { Name = "tracer design" });
        Assert.Equal(200, loaded.StatusCode);
        Assert.Equal("""{"nodes":[]}""", loaded.Data);
    }

    [Fact]
    public async Task TempSqliteSaveCreatesTheDatabaseFileUnderTheTempDirectory()
    {
        using var channel = factory.CreateGrpcChannel(Guid.NewGuid());
        var saved = await new Design.DesignClient(channel).SaveAsync(
            new SaveDesignRequest { Name = "sqlite design", Data = """{"nodes":[]}""" });
        Assert.Equal(200, saved.StatusCode);

        Assert.True(File.Exists(factory.DbFile));
        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetDirectoryName(factory.DbFile));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DesignDbContext>();
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", db.Database.ProviderName);
        Assert.Contains(factory.DbFile, db.Database.GetConnectionString());
    }

    [Fact]
    public async Task TempSqliteDisposeRemovesTheDatabaseFileAndItsSideFiles()
    {
        string dbFile;
        using (var own = new AdsServerFactory())
        {
            dbFile = own.DbFile;
            using var channel = own.CreateGrpcChannel(Guid.NewGuid());
            var saved = await new Design.DesignClient(channel).SaveAsync(
                new SaveDesignRequest { Name = "lifecycle", Data = "{}" });
            Assert.Equal(200, saved.StatusCode);

            Assert.True(File.Exists(dbFile));
            Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetDirectoryName(dbFile));
        }

        // The glob covers the database and its -wal, -shm and -journal files, and matches
        // only this factory's own unique name.
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(dbFile) + "*"));
    }

    [Fact]
    public async Task TempSqliteTwoHostsDoNotShareDataAndEachGetsItsOwnFile()
    {
        using var first = new AdsServerFactory();
        using var second = new AdsServerFactory();
        var user = Guid.NewGuid();

        using var firstChannel = first.CreateGrpcChannel(user);
        Assert.Equal(200, (await new Design.DesignClient(firstChannel).SaveAsync(
            new SaveDesignRequest { Name = "shared name", Data = "{}" })).StatusCode);

        using var secondChannel = second.CreateGrpcChannel(user);
        var list = await new Design.DesignClient(secondChannel).GetSavedAsync(new Empty());
        Assert.Equal(200, list.StatusCode);
        Assert.Empty(list.Names);

        Assert.NotEqual(first.DbFile, second.DbFile);
        Assert.True(File.Exists(first.DbFile));
        Assert.True(File.Exists(second.DbFile));
    }

    [Fact]
    public async Task TempSqliteInMemoryHostsDoNotShareAStoreEither()
    {
        using var first = new AdsServerFactory { DatabaseProviderName = "InMemory" };
        using var second = new AdsServerFactory { DatabaseProviderName = "InMemory" };
        var user = Guid.NewGuid();

        using var firstChannel = first.CreateGrpcChannel(user);
        Assert.Equal(200, (await new Design.DesignClient(firstChannel).SaveAsync(
            new SaveDesignRequest { Name = "shared name", Data = "{}" })).StatusCode);

        using var secondChannel = second.CreateGrpcChannel(user);
        var list = await new Design.DesignClient(secondChannel).GetSavedAsync(new Empty());
        Assert.Equal(200, list.StatusCode);
        Assert.Empty(list.Names);

        Assert.NotEqual(first.InMemoryName, second.InMemoryName);
        Assert.False(File.Exists(first.DbFile));
        Assert.False(File.Exists(second.DbFile));
    }

    [Fact]
    public async Task DeploySaveSubscriptionWithoutAKeyVaultAnswers503InBand()
    {
        // The real CryptoService with the checked-in sample vault name reports the missing
        // vault from its first use, before any network call.
        using var channel = factory.CreateGrpcChannel(Guid.NewGuid());

        var response = await new Deploy.DeployClient(channel).SaveSubscriptionInfoAsync(NewSubscription());

        Assert.Equal(503, response.StatusCode);
    }

    [Fact]
    public async Task DeploySaveSubscriptionWithAFakeCryptoServiceStoresTheLink()
    {
        using var withFake = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ICryptoService>(new FakeCryptoService())));
        using var channel = withFake.CreateGrpcChannel(Guid.NewGuid());
        var deploy = new Deploy.DeployClient(channel);
        var request = NewSubscription();

        var saved = await deploy.SaveSubscriptionInfoAsync(request);
        Assert.Equal(200, saved.StatusCode);

        var loaded = await deploy.LoadSubscriptionInfoAsync(new Empty());
        Assert.Equal(200, loaded.StatusCode);
        var link = Assert.Single(loaded.SubscriptionData);
        Assert.Equal(request.SubscriptionId, link.SubscriptionId);
        Assert.Equal(request.SubscriptionName, link.SubscriptionName);
    }

    private static SubscriptionInfo NewSubscription() => new()
    {
        SubscriptionId = Guid.NewGuid().ToString(),
        TenantId = Guid.NewGuid().ToString(),
        ClientId = Guid.NewGuid().ToString(),
        ClientSecret = "secret",
        SubscriptionName = "crypto seam",
    };

    [Fact]
    public async Task FailureAnonymousCallerIsUnauthenticated()
    {
        using var channel = factory.CreateGrpcChannel();
        var design = new Design.DesignClient(channel);

        var ex = await Assert.ThrowsAsync<RpcException>(() => design.GetSavedAsync(new Empty()).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task FailureAuthenticatedCallerWithoutTheRequiredScopeIsPermissionDenied()
    {
        using var channel = factory.CreateGrpcChannel(Guid.NewGuid(), withScope: false);
        var design = new Design.DesignClient(channel);
        var deploy = new Deploy.DeployClient(channel);

        var designEx = await Assert.ThrowsAsync<RpcException>(() => design.GetSavedAsync(new Empty()).ResponseAsync);
        var deployEx = await Assert.ThrowsAsync<RpcException>(() => deploy.GetGuardrailPolicyAsync(new Empty()).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, designEx.StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, deployEx.StatusCode);
    }

    [Fact]
    public async Task FailureHandlerFaultIsAnUnknownStatusAndTheHostKeepsServing()
    {
        // Owns its host: the shared class fixture must not be damaged.
        using var ownFactory = new AdsServerFactory();
        _ = ownFactory.Services; // starts the host; the bootstrapper creates the schema

        using var channel = ownFactory.CreateGrpcChannel(Guid.NewGuid());
        var design = new Design.DesignClient(channel);

        using (var scope = ownFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DesignDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE AdsDesigns");
        }

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            design.SaveAsync(new SaveDesignRequest { Name = "a", Data = "{}" }).ResponseAsync);
        Assert.Equal(StatusCode.Unknown, ex.StatusCode);

        // The host survived: other services on the same host still answer.
        Assert.Equal(200, (await new Deploy.DeployClient(channel).GetGuardrailPolicyAsync(new Empty())).StatusCode);

        using var anonymous = ownFactory.CreateGrpcChannel();
        var prices = await new Cost.CostClient(anonymous).GetRetailPricesAsync(new RetailPriceRequest());
        Assert.Equal(400, prices.StatusCode);
    }

    [Fact]
    public void BootsTestScopeMatchesTheConfiguredScope()
    {
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        Assert.Equal(TestAuthHandler.ScopeValue, configuration["AzureAd:Scopes"]);
    }

    [Fact]
    public void ReflectionIsNotMappedOutsideDevelopment()
    {
        var reflection = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(e => e.Metadata.GetMetadata<GrpcMethodMetadata>())
            .Where(m => m is not null && m.Method.ServiceName.Contains("Reflection", StringComparison.Ordinal));

        Assert.Empty(reflection);
    }

    [Fact]
    public void ReflectionInDevelopmentRequiresAnAuthenticatedCaller()
    {
        // A Development environment name that leaks onto a deployed host must not publish the
        // service surface anonymously.
        using var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));

        var reflection = development.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.GetMetadata<GrpcMethodMetadata>() is { } m
                        && m.Method.ServiceName.Contains("Reflection", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(reflection);
        Assert.All(reflection, e => Assert.NotEmpty(e.Metadata.GetOrderedMetadata<IAuthorizeData>()));
    }

    [Fact]
    public async Task EveryServiceIsReachable()
    {
        var services = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(e => e.Metadata.GetMetadata<GrpcMethodMetadata>())
            .Where(m => m is not null)
            .Select(m => m!.Method.ServiceName)
            .Distinct()
            .Order()
            .ToArray();
        Assert.Equal(["Cost", "Deploy", "Design"], services);

        using var authenticated = factory.CreateGrpcChannel(Guid.NewGuid());
        Assert.Equal(200, (await new Design.DesignClient(authenticated).GetSavedAsync(new Empty())).StatusCode);
        Assert.Equal(200, (await new Deploy.DeployClient(authenticated).GetGuardrailPolicyAsync(new Empty())).StatusCode);

        // Cost is anonymous by design; an empty request is refused before any call to the price API.
        using var anonymous = factory.CreateGrpcChannel();
        var prices = await new Cost.CostClient(anonymous).GetRetailPricesAsync(new RetailPriceRequest());
        Assert.Equal(400, prices.StatusCode);
    }
}

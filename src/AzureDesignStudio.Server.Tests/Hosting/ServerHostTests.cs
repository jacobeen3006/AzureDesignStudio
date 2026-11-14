using AzureDesignStudio.Services;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using AzureDesignStudio.Services.Models;
using Microsoft.AspNetCore.Routing;
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
    public void BootsTestScopeMatchesTheConfiguredScope()
    {
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        Assert.Equal(TestAuthHandler.ScopeValue, configuration["AzureAd:Scopes"]);
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

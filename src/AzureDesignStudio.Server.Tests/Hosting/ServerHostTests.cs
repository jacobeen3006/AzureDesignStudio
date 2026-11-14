using AzureDesignStudio.Services;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
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
}

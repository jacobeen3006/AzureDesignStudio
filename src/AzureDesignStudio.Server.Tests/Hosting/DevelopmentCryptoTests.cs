using AzureDesignStudio.Services;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>
/// The development crypto provider on a real host: a subscription is registered, listed and its
/// secret decrypted with no vault, no Azure credential and no network (CRED-01, D-13), and
/// Production refuses development keys (CRED-02, D-10).
/// </summary>
public class DevelopmentCryptoTests(AdsServerFactory factory) : IClassFixture<AdsServerFactory>
{
    private static SubscriptionInfo NewSubscription(string secret = "dev-secret-value") => new()
    {
        SubscriptionId = Guid.NewGuid().ToString(),
        TenantId = Guid.NewGuid().ToString(),
        ClientId = Guid.NewGuid().ToString(),
        ClientSecret = secret,
        SubscriptionName = "development crypto",
    };

    private static string NewKeyRingDirectory() =>
        Path.Combine(Path.GetTempPath(), $"ads-dev-keys-{Guid.NewGuid():N}");

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder must not fail the run.
        }
    }

    [Fact]
    public async Task TracerDevelopmentProviderStoresListsAndDecryptsWithNoVaultAndNoCredential()
    {
        var keyRing = NewKeyRingDirectory();
        try
        {
            using var host = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Crypto:Provider", "Development");
                builder.UseSetting("Crypto:DevelopmentKeyRingPath", keyRing);
            });
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            Assert.False(ServiceTools.IsConfigured(configuration["KeyVaultName"]));
            Assert.False(ServiceTools.IsConfigured(configuration["KeyVaultKeyName"]));

            var user = Guid.NewGuid();
            using var channel = host.CreateGrpcChannel(user);
            var deploy = new Deploy.DeployClient(channel);
            var request = NewSubscription();

            var saved = await deploy.SaveSubscriptionInfoAsync(request);
            Assert.Equal(200, saved.StatusCode);

            var loaded = await deploy.LoadSubscriptionInfoAsync(new Empty());
            Assert.Equal(200, loaded.StatusCode);
            var link = Assert.Single(loaded.SubscriptionData);
            Assert.Equal(request.SubscriptionId, link.SubscriptionId);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DesignDbContext>();
            var stored = await db.AzureSubscriptions.AsNoTracking()
                .SingleAsync(s => s.UserId == user && s.SubscriptionId == Guid.Parse(request.SubscriptionId));
            Assert.StartsWith("dev1:", stored.ClientSecret);
            Assert.NotEqual(request.ClientSecret, stored.ClientSecret);
            Assert.DoesNotContain(request.ClientSecret, stored.ClientSecret);

            var crypto = host.Services.GetRequiredService<ICryptoService>();
            Assert.Equal(request.ClientSecret, await crypto.Decrypt(stored.ClientSecret));
            Assert.True(Directory.Exists(keyRing));
        }
        finally
        {
            DeleteQuietly(keyRing);
        }
    }
}

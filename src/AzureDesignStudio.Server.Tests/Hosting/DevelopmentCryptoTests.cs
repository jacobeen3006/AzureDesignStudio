using System.Security.Cryptography;
using System.Text.Json;
using AzureDesignStudio.Server.Services;
using AzureDesignStudio.Services;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.DataProtection;
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

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is { } inner)
            ex = inner;
        return ex;
    }

    private static bool Mentions(Exception ex, string text)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e.Message.Contains(text, StringComparison.Ordinal))
                return true;
        return false;
    }

    [Fact]
    public void ProductionHostWithTheDevelopmentProviderDoesNotStart()
    {
        var keyRing = NewKeyRingDirectory();
        try
        {
            using var host = factory.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("Crypto:Provider", "Development");
                builder.UseSetting("Crypto:DevelopmentKeyRingPath", keyRing);
            });

            var failure = Assert.ThrowsAny<Exception>(() => host.Services);

            Assert.IsType<InvalidOperationException>(Innermost(failure));
            Assert.True(Mentions(failure, "Production") && Mentions(failure, "KeyVault"), failure.ToString());
            Assert.False(Directory.Exists(keyRing));
        }
        finally
        {
            DeleteQuietly(keyRing);
        }
    }

    [Fact]
    public async Task ProductionHostWithKeyVaultAndNoVaultBootsAndAnswers503InBand()
    {
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Crypto:Provider", "KeyVault");
        });
        using var channel = host.CreateGrpcChannel(Guid.NewGuid());

        var response = await new Deploy.DeployClient(channel).SaveSubscriptionInfoAsync(NewSubscription());

        Assert.Equal(503, response.StatusCode);
    }

    private static CryptoPair NewService(string directory)
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(directory), b => b.SetApplicationName("tests"));
        return new CryptoPair(new DevelopmentCryptoService(provider.CreateProtector(DevelopmentCryptoService.Purpose)));
    }

    private sealed record CryptoPair(DevelopmentCryptoService Service);

    [Fact]
    public async Task DevelopmentCiphertextIsPrefixedAndNeverValidBase64()
    {
        var directory = NewKeyRingDirectory();
        try
        {
            var service = NewService(directory).Service;

            var encrypted = await service.Encrypt("plain secret");

            Assert.StartsWith("dev1:", encrypted);
            Assert.DoesNotContain("plain secret", encrypted);
            // The Key Vault service base64-decodes first, so it fails into the broken-link path.
            Assert.Throws<FormatException>(() => Convert.FromBase64String(encrypted));
            Assert.Equal("plain secret", await service.Decrypt(encrypted));
        }
        finally
        {
            DeleteQuietly(directory);
        }
    }

    [Fact]
    public async Task DevelopmentServiceRefusesUnprefixedAndTamperedPayloads()
    {
        var directory = NewKeyRingDirectory();
        try
        {
            var service = NewService(directory).Service;
            var encrypted = await service.Encrypt("plain secret");

            await Assert.ThrowsAsync<CryptographicException>(
                () => service.Decrypt(Convert.ToBase64String("vault ciphertext"u8.ToArray())));
            await Assert.ThrowsAsync<CryptographicException>(
                () => service.Decrypt(encrypted + "AAAA"));
            await Assert.ThrowsAsync<CryptographicException>(
                () => service.Decrypt(encrypted[..^6]));
            await Assert.ThrowsAsync<CryptographicException>(
                () => service.Decrypt("dev1:not base64 at all !!"));
        }
        finally
        {
            DeleteQuietly(directory);
        }
    }

    [Fact]
    public async Task ADevelopmentBlobThatIsUnreadableMakesTheLinkA404()
    {
        var keyRing = NewKeyRingDirectory();
        try
        {
            using var host = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Crypto:Provider", "Development");
                builder.UseSetting("Crypto:DevelopmentKeyRingPath", keyRing);
            });
            var user = Guid.NewGuid();
            using var channel = host.CreateGrpcChannel(user);
            var deploy = new Deploy.DeployClient(channel);
            var request = NewSubscription();
            Assert.Equal(200, (await deploy.SaveSubscriptionInfoAsync(request)).StatusCode);

            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DesignDbContext>();
                var row = await db.AzureSubscriptions.SingleAsync(s => s.UserId == user);
                row.ClientSecret = "not-a-development-blob";
                await db.SaveChangesAsync();
            }

            var projection = await deploy.GetResourceGroupsAsync(new GetRgsRequest
            {
                SubscriptionId = request.SubscriptionId,
            });
            Assert.Equal(404, projection.StatusCode);
        }
        finally
        {
            DeleteQuietly(keyRing);
        }
    }

    [Fact]
    public void KeyRingDirectoryIsCreatedOnFirstUseAndIsUserOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return; // DPAPI protects the keys on Windows; the mode applies to Unix only.

        var keyRing = Path.Combine(NewKeyRingDirectory(), "nested", "keys");
        try
        {
            using var host = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Crypto:Provider", "Development");
                builder.UseSetting("Crypto:DevelopmentKeyRingPath", keyRing);
            });
            Assert.False(Directory.Exists(keyRing));

            _ = host.Services.GetRequiredService<ICryptoService>();

            Assert.True(Directory.Exists(keyRing));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(keyRing));
        }
        finally
        {
            DeleteQuietly(Path.GetDirectoryName(Path.GetDirectoryName(keyRing))!);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root (global.json) was not found.");
    }

    [Fact]
    public void OnlyDevelopmentLaunchProfilesEnableTheDevelopmentProvider()
    {
        var path = Path.Combine(RepositoryRoot(), "src/AzureDesignStudio.Server/Properties/launchSettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var profiles = document.RootElement.GetProperty("profiles");

        var enabling = new List<string>();
        foreach (var profile in profiles.EnumerateObject())
        {
            var variables = profile.Value.TryGetProperty("environmentVariables", out var v) ? v : default;
            string? Get(string name) =>
                variables.ValueKind == JsonValueKind.Object && variables.TryGetProperty(name, out var value)
                    ? value.GetString() : null;

            if (string.Equals(Get("Crypto__Provider"), "Development", StringComparison.OrdinalIgnoreCase))
            {
                enabling.Add(profile.Name);
                Assert.Equal("Development", Get("ASPNETCORE_ENVIRONMENT"));
            }
        }

        Assert.Contains("AzureDesignStudio.Server", enabling);
        Assert.Contains("IIS Express", enabling);
        Assert.DoesNotContain("Docker", enabling);
    }

    [Theory]
    [InlineData("src/AzureDesignStudio.Server/appsettings.json")]
    [InlineData("src/AzureDesignStudio.Server/appsettings.Development.json")]
    [InlineData("src/AzureDesignStudio.Server/appsettings.Production.json")]
    public void CheckedInSettingsNeverSelectTheDevelopmentProvider(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot(), relativePath);
        if (!File.Exists(path))
            return;

        using var document = JsonDocument.Parse(File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Assert.False(
            document.RootElement.TryGetProperty("Crypto", out _) ||
            document.RootElement.TryGetProperty("Crypto:Provider", out _),
            $"{relativePath} must not select a crypto provider.");
    }

    [Fact]
    public void TheDockerfileNeverSelectsTheDevelopmentProvider()
    {
        var path = Path.Combine(RepositoryRoot(), "src/AzureDesignStudio.Server/Dockerfile");
        Assert.DoesNotContain("Crypto__Provider", File.ReadAllText(path));
    }
}

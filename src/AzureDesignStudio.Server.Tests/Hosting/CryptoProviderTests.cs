using AzureDesignStudio.Server.Services;
using AzureDesignStudio.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>The rules of <see cref="CryptoProvider.Register"/>. The descriptors are inspected
/// without building a provider, so the Key Vault service is never constructed.</summary>
public class CryptoProviderTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static ServiceDescriptor Registered(IConfiguration configuration, string environment = "Testing")
    {
        var services = new ServiceCollection();
        CryptoProvider.Register(services, configuration, new FakeEnvironment(environment));
        return Assert.Single(services, d => d.ServiceType == typeof(ICryptoService));
    }

    [Fact]
    public void UnsetValueRegistersTheKeyVaultService()
    {
        var descriptor = Registered(Config());

        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(CryptoService), descriptor.ImplementationType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("KeyVault")]
    [InlineData("keyvault")]
    [InlineData(" KeyVault ")]
    public void BlankAndKeyVaultValuesRegisterTheKeyVaultService(string value)
    {
        var descriptor = Registered(Config((CryptoProvider.Key, value)));

        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(CryptoService), descriptor.ImplementationType);
    }

    [Fact]
    public void KeyIsTheOperatorVisibleSetting() => Assert.Equal("Crypto:Provider", CryptoProvider.Key);

    [Fact]
    public void AnyOtherValueIsRejectedWithAReason()
    {
        var services = new ServiceCollection();

        var failure = Assert.Throws<InvalidOperationException>(
            () => CryptoProvider.Register(services, Config(("Crypto:Provider", "DevKeys")), new FakeEnvironment("Testing")));

        Assert.Contains("Crypto:Provider", failure.Message);
        Assert.Contains("DevKeys", failure.Message);
        Assert.Contains("KeyVault", failure.Message);
        Assert.Contains("Development", failure.Message);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("development")]
    [InlineData(" Development ")]
    public void DevelopmentValueRegistersASingletonBuiltByAFactory(string value)
    {
        var descriptor = Registered(Config((CryptoProvider.Key, value)));

        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.NotNull(descriptor.ImplementationFactory);
        Assert.Null(descriptor.ImplementationType);
    }

    [Fact]
    public void DevelopmentInProductionIsRefusedBeforeAnythingIsRegistered()
    {
        var services = new ServiceCollection();

        var failure = Assert.Throws<InvalidOperationException>(
            () => CryptoProvider.Register(services, Config((CryptoProvider.Key, "Development")), new FakeEnvironment("Production")));

        Assert.Contains("Production", failure.Message);
        Assert.Contains("KeyVault", failure.Message);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public void DevelopmentIsAllowedOutsideProduction(string environment)
    {
        var descriptor = Registered(Config((CryptoProvider.Key, "Development")), environment);

        Assert.NotNull(descriptor.ImplementationFactory);
    }

    [Fact]
    public void KeyVaultIsStillAllowedInProduction()
    {
        var descriptor = Registered(Config((CryptoProvider.Key, "KeyVault")), "Production");

        Assert.Equal(typeof(CryptoService), descriptor.ImplementationType);
    }
}

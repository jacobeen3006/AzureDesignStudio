using AzureDesignStudio.Services;
using Microsoft.AspNetCore.DataProtection;

namespace AzureDesignStudio.Server.Services;

/// <summary>
/// Registers <see cref="ICryptoService"/> from <c>Crypto:Provider</c>: <c>KeyVault</c> (default; the
/// lazy vault service) or <c>Development</c> (Data Protection with a file-system key ring, for a
/// developer working offline).
/// </summary>
internal static class CryptoProvider
{
    public const string Key = "Crypto:Provider";
    public const string KeyVault = "KeyVault";
    public const string Development = "Development";

    /// <summary>Optional override of the development key-ring folder.</summary>
    public const string DevelopmentKeyRingPathKey = "Crypto:DevelopmentKeyRingPath";

    /// <summary>Application name of the standalone development provider.</summary>
    public const string DevelopmentApplicationName = "AzureDesignStudio.Dev";

    public static void Register(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var value = configuration[Key];
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), KeyVault, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ICryptoService, CryptoService>();
            return;
        }

        if (string.Equals(value.Trim(), Development, StringComparison.OrdinalIgnoreCase))
        {
            var path = KeyRingPath(configuration);
            // Standalone, not AddDataProtection: this keeps the development key ring out of the
            // application-wide key repository that antiforgery and cookies use.
            services.AddSingleton<ICryptoService>(_ =>
            {
                var provider = DataProtectionProvider.Create(
                    new DirectoryInfo(path),
                    builder => builder.SetApplicationName(DevelopmentApplicationName));
                return new DevelopmentCryptoService(provider.CreateProtector(DevelopmentCryptoService.Purpose));
            });
            return;
        }

        throw new InvalidOperationException(
            $"{Key} is \"{value}\", which is not a crypto provider. The accepted values are {KeyVault} and {Development}.");
    }

    private static string KeyRingPath(IConfiguration configuration)
    {
        var configured = configuration[DevelopmentKeyRingPathKey];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AzureDesignStudio", "dev-keys")
            : configured;
    }
}

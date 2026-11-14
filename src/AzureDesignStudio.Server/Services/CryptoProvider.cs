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
            // First thing, before anything is registered: a Production host stops during startup
            // (CRED-02, D-10). Development keys never protect production secrets.
            if (environment.IsProduction())
                throw new InvalidOperationException(
                    $"{Key} is \"{Development}\", which is never allowed in the Production environment. Use {KeyVault}.");

            var path = KeyRingPath(configuration);
            // Standalone, not AddDataProtection: this keeps the development key ring out of the
            // application-wide key repository that antiforgery and cookies use.
            services.AddSingleton<ICryptoService>(_ =>
            {
                var directory = EnsureKeyRingDirectory(path);
                var provider = DataProtectionProvider.Create(directory, ConfigureKeyRing);
                return new DevelopmentCryptoService(provider.CreateProtector(DevelopmentCryptoService.Purpose));
            });
            return;
        }

        throw new InvalidOperationException(
            $"{Key} is \"{value}\", which is not a crypto provider. The accepted values are {KeyVault} and {Development}.");
    }

    /// <summary>Creates the key-ring folder on first use. On Unix it is user-only (mode 0700), set
    /// explicitly because the creation mode is filtered by the umask. A folder that already exists
    /// is the operator's and is left alone. The path is never logged.</summary>
    private static DirectoryInfo EnsureKeyRingDirectory(string path)
    {
        if (Directory.Exists(path))
            return new DirectoryInfo(path);

        if (OperatingSystem.IsWindows())
            return Directory.CreateDirectory(path);

        const UnixFileMode userOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var created = Directory.CreateDirectory(path, userOnly);
        File.SetUnixFileMode(created.FullName, userOnly);
        return created;
    }

    /// <summary>Windows keys are DPAPI-protected (current user). macOS and Linux keys are plaintext
    /// XML in a user-only folder; that residual is documented for a developer-only mode.</summary>
    private static void ConfigureKeyRing(IDataProtectionBuilder builder)
    {
        builder.SetApplicationName(DevelopmentApplicationName);
        if (OperatingSystem.IsWindows())
            builder.ProtectKeysWithDpapi();
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

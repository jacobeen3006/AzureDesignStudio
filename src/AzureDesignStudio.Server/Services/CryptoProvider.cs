using AzureDesignStudio.Services;

namespace AzureDesignStudio.Server.Services;

/// <summary>
/// Registers <see cref="ICryptoService"/> from <c>Crypto:Provider</c>. The only accepted value
/// today is <c>KeyVault</c>; a development provider and the rule that production refuses it are
/// Phase 5 work (CRED-01, CRED-02).
/// </summary>
internal static class CryptoProvider
{
    public const string Key = "Crypto:Provider";
    public const string KeyVault = "KeyVault";

    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        var value = configuration[Key];
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), KeyVault, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ICryptoService, CryptoService>();
            return;
        }

        throw new InvalidOperationException(
            $"{Key} is \"{value}\", which is not a crypto provider. The only accepted value is {KeyVault}.");
    }
}

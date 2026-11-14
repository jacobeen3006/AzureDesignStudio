using System.Security.Cryptography;
using AzureDesignStudio.Services;
using Microsoft.AspNetCore.DataProtection;

namespace AzureDesignStudio.Server.Services;

/// <summary>
/// The development <see cref="ICryptoService"/>: subscription secrets protected by ASP.NET Core Data
/// Protection with a key ring on the developer's disk (D-13). It never touches a vault, an Azure
/// credential or the network, so a developer can register and exercise a subscription offline.
/// It is selected only by <see cref="CryptoProvider"/> (<c>Crypto:Provider</c> = Development) and
/// is refused in the Production environment.
/// </summary>
/// <remarks>
/// Output carries the <see cref="Prefix"/>. Key Vault ciphertext is base64, whose alphabet has no
/// colon, so the vault service fails on a development value with a <see cref="FormatException"/>;
/// this service refuses text without the prefix. Either way <c>DeployService.GetSubscriptionInfo</c>
/// answers "link the subscription again" (404) instead of decrypting garbage.
/// </remarks>
internal sealed class DevelopmentCryptoService(IDataProtector protector) : ICryptoService
{
    /// <summary>Marks a development ciphertext; never valid base64.</summary>
    public const string Prefix = "dev1:";

    /// <summary>The Data Protection purpose string of subscription secrets.</summary>
    public const string Purpose = "AzureDesignStudio.SubscriptionSecret.v1";

    public Task<string> Encrypt(string plainText) =>
        Task.FromResult(Prefix + protector.Protect(plainText));

    public Task<string> Decrypt(string encryptedText)
    {
        if (encryptedText is null || !encryptedText.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException(
                "The stored secret was not written by the development crypto provider; link the subscription again.");

        try
        {
            return Task.FromResult(protector.Unprotect(encryptedText[Prefix.Length..]));
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("The stored development secret is not readable.", ex);
        }
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Persists the MSAL user token cache in the login Keychain. On Windows the WAM broker keeps
/// the accounts; macOS has no broker here, and an unserialized MSAL cache is in memory only -
/// every launch would start signed out.
/// </summary>
internal static class KeychainTokenCache
{
    private const string Service = SecretStore.TargetPrefix + "MsalTokenCache";
    private static readonly object Gate = new();

    public static void Bind(ITokenCache cache, ILogger logger)
    {
        cache.SetBeforeAccess(args =>
        {
            byte[]? data;
            lock (Gate)
                data = KeychainSecretStore.TryGet(Service);
            args.TokenCache.DeserializeMsalV3(data, shouldClearExistingCache: true);
        });
        cache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged)
                return;
            try
            {
                var data = args.TokenCache.SerializeMsalV3();
                lock (Gate)
                    KeychainSecretStore.Save(Service, "msal", data);
            }
            catch (InvalidOperationException e)
            {
                // The token is still good for this run; only the next launch has to sign in again.
                logger.LogWarning(e, "Could not save the sign-in to the Keychain");
            }
        });
    }
}

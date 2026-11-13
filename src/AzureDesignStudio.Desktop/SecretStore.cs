namespace AzureDesignStudio.Desktop;

/// <summary>
/// The operating system's own secret store: Windows Credential Manager on Windows, the
/// login Keychain on macOS. Service principal secrets and (on macOS) the MSAL token cache
/// live here, never in appsettings or plain files. Entries are namespaced under
/// <c>AzureDesignStudio/</c>.
/// </summary>
public static class SecretStore
{
    public const string TargetPrefix = "AzureDesignStudio/";

    /// <summary>Where the secrets live, for messages that tell the user where to look.</summary>
    public const string Name =
#if WINDOWS
        "Windows Credential Manager";
#else
        "macOS Keychain";
#endif

    /// <summary>Stores <paramref name="secret"/> under <c>AzureDesignStudio/&lt;name&gt;</c>, replacing any earlier value.</summary>
    public static void Save(string name, string userName, string secret) =>
#if WINDOWS
        WindowsSecretStore.Save(name, userName, secret);
#elif MACCATALYST
        KeychainSecretStore.Save(TargetPrefix + name, userName, System.Text.Encoding.UTF8.GetBytes(secret));
#else
        throw new PlatformNotSupportedException();
#endif

    /// <summary>Returns the stored secret, or null when there is none.</summary>
    public static string? TryGet(string name) =>
#if WINDOWS
        WindowsSecretStore.TryGet(name);
#elif MACCATALYST
        KeychainSecretStore.TryGet(TargetPrefix + name) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;
#else
        null;
#endif

    public static bool Delete(string name) =>
#if WINDOWS
        WindowsSecretStore.Delete(name);
#elif MACCATALYST
        KeychainSecretStore.Delete(TargetPrefix + name);
#else
        false;
#endif
}

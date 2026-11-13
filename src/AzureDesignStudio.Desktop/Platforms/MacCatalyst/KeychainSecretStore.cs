using System.Runtime.InteropServices;
using System.Text;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Generic-password items in the user's login Keychain, through the file-based keychain API
/// of Security.framework. That API needs no entitlement and no team-signed build: the
/// iOS-style SecItem calls a Catalyst app would normally use go to the data-protection
/// keychain, which refuses (errSecMissingEntitlement) any app not signed with a
/// keychain-access-groups entitlement - i.e. every build installed from the .dmg.
/// The item's service name is the full key; the account name is informational.
/// </summary>
internal static class KeychainSecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain,
        uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
        uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray,
        uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[]? accountName,
        out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    public static void Save(string service, string account, byte[] secret)
    {
        var serviceBytes = Encoding.UTF8.GetBytes(service);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes, 0, null,
            out _, out var existing, out var item);
        if (status == ErrSecSuccess)
        {
            try
            {
                SecKeychainItemFreeContent(IntPtr.Zero, existing);
                Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)secret.Length, secret), "update", service);
            }
            finally
            {
                CFRelease(item);
            }
            return;
        }
        if (status != ErrSecItemNotFound)
            Check(status, "look up", service);

        var accountBytes = Encoding.UTF8.GetBytes(account);
        Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes,
            (uint)accountBytes.Length, accountBytes, (uint)secret.Length, secret, out var added), "add", service);
        CFRelease(added);
    }

    /// <summary>The stored bytes, or null when the item does not exist (or the user denied access).</summary>
    public static byte[]? TryGet(string service)
    {
        var serviceBytes = Encoding.UTF8.GetBytes(service);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes, 0, null,
            out var length, out var data, out var item);
        if (status != ErrSecSuccess)
            return null;
        try
        {
            var bytes = new byte[length];
            if (length > 0)
                Marshal.Copy(data, bytes, 0, (int)length);
            return bytes;
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            CFRelease(item);
        }
    }

    public static bool Delete(string service)
    {
        var serviceBytes = Encoding.UTF8.GetBytes(service);
        if (SecKeychainFindGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes, 0, null,
                out _, out var data, out var item) != ErrSecSuccess)
            return false;
        try
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            return SecKeychainItemDelete(item) == ErrSecSuccess;
        }
        finally
        {
            CFRelease(item);
        }
    }

    private static void Check(int status, string what, string service)
    {
        if (status != ErrSecSuccess)
            throw new InvalidOperationException($"Keychain {what} failed for '{service}' (OSStatus {status}).");
    }
}

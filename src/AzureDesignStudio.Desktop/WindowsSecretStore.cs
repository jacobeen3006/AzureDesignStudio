using System.Runtime.InteropServices;
using System.Text;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Secret storage backed by Windows Credential Manager (plan 6.2): tokens and service
/// principal secrets persist as generic credentials (DPAPI-encrypted by the OS), never
/// in appsettings or plain files. Targets are namespaced under <c>AzureDesignStudio/</c>.
/// </summary>
public static class WindowsSecretStore
{
    public const string TargetPrefix = "AzureDesignStudio/";

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    /// <summary>Stores <paramref name="secret"/> under <c>AzureDesignStudio/&lt;name&gt;</c>.</summary>
    public static void Save(string name, string userName, string secret)
    {
        var blob = Marshal.StringToHGlobalUni(secret);
        try
        {
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = TargetPrefix + name,
                UserName = userName,
                CredentialBlob = blob,
                CredentialBlobSize = Encoding.Unicode.GetByteCount(secret),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException($"Credential Manager write failed for '{name}' ({Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    /// <summary>Returns the stored secret, or null when the target does not exist.</summary>
    public static string? TryGet(string name)
    {
        if (!CredRead(TargetPrefix + name, CRED_TYPE_GENERIC, 0, out var ptr))
            return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
                return null;
            return Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static bool Delete(string name) => CredDelete(TargetPrefix + name, CRED_TYPE_GENERIC, 0);
}

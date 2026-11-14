namespace AzureDesignStudio.Desktop;

/// <summary>macOS-specific start-up adjustments.</summary>
internal static class MacHost
{
    /// <summary>
    /// An app started from Finder or the Dock gets launchd's PATH (/usr/bin:/bin:/usr/sbin:/sbin),
    /// not the shell's, so the Azure CLI fallback would never find <c>az</c> where Homebrew and
    /// the official installer put it.
    /// </summary>
    public static void ExtendToolPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin:/usr/sbin:/sbin";
        var parts = path.Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var dir in new[] { "/opt/homebrew/bin", "/usr/local/bin" })
        {
            if (!parts.Contains(dir) && Directory.Exists(dir))
                parts.Add(dir);
        }
        Environment.SetEnvironmentVariable("PATH", string.Join(':', parts));
    }

    /// <summary>
    /// ~/Library/Application Support/&lt;bundle id&gt;. MAUI's AppDataDirectory is the Library folder,
    /// which is a private container only inside the App Sandbox; this app is not sandboxed (see
    /// Entitlements.plist), so it would be the user's own ~/Library.
    /// </summary>
    public static string AppDataDirectory
    {
        get
        {
            var support = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = Path.Combine(support, "Library", "Application Support",
                Foundation.NSBundle.MainBundle.BundleIdentifier ?? "com.azuredesignstudio.desktop");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Read-only app content (wwwroot, ads-stencils.json): Contents/Resources of the bundle.</summary>
    public static string ContentRoot => Foundation.NSBundle.MainBundle.ResourcePath ?? AppContext.BaseDirectory;
}

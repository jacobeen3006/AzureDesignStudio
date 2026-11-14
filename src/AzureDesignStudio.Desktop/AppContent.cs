namespace AzureDesignStudio.Desktop;

/// <summary>Where the app's read-only content (wwwroot, ads-stencils.json, appsettings.json) sits on disk.</summary>
internal static class AppContent
{
    /// <summary>Windows: next to the executable. macOS: the bundle's Contents/Resources.</summary>
    public static string Root =>
#if MACCATALYST
        MacHost.ContentRoot;
#else
        AppContext.BaseDirectory;
#endif

    /// <summary>The resource palette. Windows ships a copy next to the executable; macOS reads the
    /// one inside the bundled web assets (a second copy would collide in Contents/Resources).</summary>
    public static string StencilsFile =>
#if MACCATALYST
        Path.Combine(Root, "wwwroot", "ads-stencils.json");
#else
        Path.Combine(Root, "ads-stencils.json");
#endif

    /// <summary>appsettings.json: next to the executable on Windows, in Contents/Resources on macOS.</summary>
    public static string SettingsFile => Path.Combine(Root, "appsettings.json");
}

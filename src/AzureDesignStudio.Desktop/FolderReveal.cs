using System.ComponentModel;
using System.Diagnostics;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Shows a folder, or a file inside its folder, in the platform file manager. The path travels as ONE
/// argument (no shell, no string-built command line) and must exist under the data directory.
/// </summary>
internal static class FolderReveal
{
    public static bool OpenFolder(string directory)
    {
        if (!IsUnderDataDirectory(directory) || !Directory.Exists(directory)) return false;
        return Start(OperatingSystem.IsWindows()
            ? ["explorer.exe", directory]
            : ["open", directory]);
    }

    public static bool Reveal(string file)
    {
        if (!IsUnderDataDirectory(file) || !File.Exists(file)) return false;
        return Start(OperatingSystem.IsWindows()
            ? ["explorer.exe", "/select," + file]
            : ["open", "-R", file]);
    }

    private static bool IsUnderDataDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(MauiProgram.DataDirectory)) return false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(MauiProgram.DataDirectory));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.Equals(root, StringComparison.Ordinal)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool Start(string[] command)
    {
        var info = new ProcessStartInfo(command[0]) { UseShellExecute = false };
        foreach (var argument in command.Skip(1)) info.ArgumentList.Add(argument);
        try
        {
            using var _ = Process.Start(info);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}

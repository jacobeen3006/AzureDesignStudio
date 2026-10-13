using System.Text.Json;

namespace AzureDesignStudio.Workspace;

/// <summary>
/// One-time import of the v1 file-based pending-design cache
/// (<c>%AppData%/designs/*.adsdesign.json</c>) into the SQLite workspace.
/// Imported files are removed so the same design never exists in two stores.
/// </summary>
public static class LegacyDesignImporter
{
    private record Entry(string Name, string Data, DateTimeOffset SavedAt);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    /// <summary>Returns the number of designs imported. Safe to call on every start.</summary>
    public static int ImportIfNeeded(WorkspaceDb workspace, string legacyDirectory)
    {
        if (!Directory.Exists(legacyDirectory))
            return 0;

        var imported = 0;
        var directoryEmpty = true;
        using var db = workspace.CreateContext();

        foreach (var path in Directory.EnumerateFiles(legacyDirectory, "*.adsdesign.json"))
        {
            Entry? entry = null;
            try
            {
                entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), SerializerOptions);
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                // Corrupted v1 entry: same rule as the old store - drop it, never crash.
            }

            if (entry != null && !string.IsNullOrWhiteSpace(entry.Name)
                && !db.Designs.Any(d => d.Name == entry.Name))
            {
                db.Designs.Add(new DesignRecord { Name = entry.Name, Data = entry.Data, SavedAt = entry.SavedAt });
                imported++;
            }

            try
            {
                File.Delete(path);
                directoryEmpty = Directory.EnumerateFiles(legacyDirectory).Any() == false &&
                                 Directory.EnumerateDirectories(legacyDirectory).Any() == false;
            }
            catch (IOException)
            {
                directoryEmpty = false;
            }
        }

        db.SaveChanges();

        if (directoryEmpty)
        {
            try
            {
                Directory.Delete(legacyDirectory);
            }
            catch (IOException)
            {
            }
        }

        return imported;
    }
}

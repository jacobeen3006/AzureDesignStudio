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

        foreach (var path in Directory.EnumerateFiles(legacyDirectory, "*.adsdesign.json").ToList())
        {
            Entry? entry = null;
            try
            {
                entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), SerializerOptions);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // Corrupted v1 entry: same rule as the old store - drop it, never crash.
            }

            // One design, one transaction, and the file goes only after its row is stored.
            // Saving everything at the end - after every file was already deleted - lost the
            // whole batch when a single entry failed (no Data, or a name used twice).
            if (entry is { Data: not null } && !string.IsNullOrWhiteSpace(entry.Name))
            {
                try
                {
                    using var db = workspace.CreateContext();
                    if (!db.Designs.Any(d => d.Name == entry.Name))
                    {
                        db.Designs.Add(new DesignRecord { Name = entry.Name, Data = entry.Data, SavedAt = entry.SavedAt });
                        db.SaveChanges();
                        imported++;
                    }
                }
                catch (Exception e) when (e is Microsoft.EntityFrameworkCore.DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
                {
                    // Not stored: keep the file so the next start can try again.
                    directoryEmpty = false;
                    continue;
                }
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                directoryEmpty = false;
            }
        }

        directoryEmpty = directoryEmpty
            && !Directory.EnumerateFiles(legacyDirectory).Any()
            && !Directory.EnumerateDirectories(legacyDirectory).Any();

        if (directoryEmpty)
        {
            try
            {
                Directory.Delete(legacyDirectory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return imported;
    }
}

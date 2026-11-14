using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureDesignStudio.Workspace;

/// <summary>
/// The sidecar every backup carries (<c>&lt;backup&gt;.db.manifest.json</c>): what it is, when and why it was
/// taken, which schema it holds and how to verify it.
/// </summary>
public sealed record BackupManifest(
    DateTimeOffset CreatedUtc,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] BackupReason Reason,
    string AppVersion,
    string? LatestMigrationId,
    IReadOnlyDictionary<string, long> TableCounts,
    string Sha256,
    long SizeBytes,
    string SourceFileName)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The manifest path that belongs to a backup file.</summary>
    public static string PathFor(string backupPath) => backupPath + ".manifest.json";

    public void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static BackupManifest Read(string path) =>
        JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"The backup manifest {path} is empty.");
}

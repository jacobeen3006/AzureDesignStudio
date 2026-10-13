using System.Text.Json;
using AzureDesignStudio.Services;

namespace AzureDesignStudio.Desktop
{
    /// <summary>
    /// Designs saved while the ADS server is unreachable, persisted under the app's
    /// data directory. Each file is one unsynced ("pending") design; syncing deletes it.
    /// </summary>
    public class FileSystemDesignOfflineStore : IDesignOfflineStore
    {
        private record Entry(string Name, string Data, DateTimeOffset SavedAt);

        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
        private readonly string _directory;

        public FileSystemDesignOfflineStore()
        {
            _directory = Path.Combine(FileSystem.Current.AppDataDirectory, "designs");
            Directory.CreateDirectory(_directory);
        }

        private string FilePathFor(string name)
        {
            var safe = string.Join('_', name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrEmpty(safe))
                safe = "design";
            return Path.Combine(_directory, safe + ".adsdesign.json");
        }

        public async Task SaveAsync(string name, string data)
        {
            var path = FilePathFor(name);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new Entry(name, data, DateTimeOffset.UtcNow), SerializerOptions));
            File.Move(temp, path, overwrite: true);
        }

        public async Task<string?> TryLoadAsync(string name)
        {
            var path = FilePathFor(name);
            if (!File.Exists(path))
                return null;
            var entry = await ReadAsync(path);
            return entry?.Data;
        }

        public async Task<IList<string>> ListAsync()
        {
            var names = new List<string>();
            foreach (var path in Directory.EnumerateFiles(_directory, "*.adsdesign.json"))
            {
                var entry = await ReadAsync(path);
                if (entry != null && !names.Contains(entry.Name))
                    names.Add(entry.Name);
            }
            return names;
        }

        public Task DeleteAsync(string name)
        {
            var path = FilePathFor(name);
            if (File.Exists(path))
                File.Delete(path);
            return Task.CompletedTask;
        }

        public async Task<IList<(string Name, string Data)>> ListPendingAsync()
        {
            var pending = new List<(string, string)>();
            foreach (var path in Directory.EnumerateFiles(_directory, "*.adsdesign.json"))
            {
                var entry = await ReadAsync(path);
                if (entry != null)
                    pending.Add((entry.Name, entry.Data));
            }
            return pending;
        }

        public Task ClearPendingAsync(string name) => DeleteAsync(name);

        private static async Task<Entry?> ReadAsync(string path)
        {
            try
            {
                return JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(path), SerializerOptions);
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                return null; // corrupted cache entry: ignore, never crash the save/load flow
            }
        }
    }
}

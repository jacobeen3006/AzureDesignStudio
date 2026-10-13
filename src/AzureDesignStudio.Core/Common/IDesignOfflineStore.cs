namespace AzureDesignStudio.Core.Common;

/// <summary>
/// Host-provided local persistence for designs while the design server is
/// unreachable (Desktop: SQLite workspace; WASM: not registered).
/// Designs saved here are "pending": they sync to the server on the next
/// successful server interaction, which clears them.
/// </summary>
public interface IDesignOfflineStore
{
    Task SaveAsync(string name, string data);
    Task<string?> TryLoadAsync(string name);
    Task<IList<string>> ListAsync();
    Task DeleteAsync(string name);
    Task<IList<(string Name, string Data)>> ListPendingAsync();
    Task ClearPendingAsync(string name);
}

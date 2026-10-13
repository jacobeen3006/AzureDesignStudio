namespace AzureDesignStudio.Services
{
    /// <summary>
    /// Host-provided local persistence for designs while the design server is
    /// unreachable (Desktop: file cache under AppData; WASM: not registered).
    /// Files written here are "pending": they sync to the server on the next
    /// successful server interaction.
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
}

namespace AzureDesignStudio.Services;

/// <summary>
/// Shared command surface for the shell (command palette, inspector rail).
/// The menu owns the handlers; the shell only invokes them by key.
/// </summary>
public sealed record AdsCommand(
    string Key,
    string Label,
    string Description,
    string Group,
    string Glyph,
    Func<Task> Execute);

public sealed class AdsCommandService
{
    private readonly Dictionary<string, AdsCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    /// <summary>Raised by the header trigger or Ctrl+K; the palette listens.</summary>
    public event Action? PaletteRequested;

    public IReadOnlyList<AdsCommand> Commands => _commands.Values
        .OrderBy(c => c.Group, StringComparer.OrdinalIgnoreCase)
        .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public void RegisterRange(IEnumerable<AdsCommand> commands)
    {
        foreach (var command in commands)
            _commands[command.Key] = command;

        Changed?.Invoke();
    }

    public bool TryGet(string key, out AdsCommand command) => _commands.TryGetValue(key, out command!);

    public void RequestPalette() => PaletteRequested?.Invoke();
}

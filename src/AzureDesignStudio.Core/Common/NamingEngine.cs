using System.Security.Cryptography;
using System.Text;

namespace AzureDesignStudio.Core.Common;

/// <summary>
/// What a name for one resource family may look like (v2 plan 10.4). Charset/length mirror
/// the ARM rules the canvas validators enforce; <see cref="GloballyUnique"/> is the piece the
/// canvas can't know - names that collide tenant-wide (blob endpoints, website hosts) need
/// entropy, not just a tidy prefix. Live uniqueness verification is the deferred online half.
/// </summary>
public sealed record NamingProfile(int MinLength, int MaxLength, string AllowedChars, bool Lowercase, bool GloballyUnique)
{
    public static readonly NamingProfile Generic = new(1, 64, AlphaNumeric + "-._", Lowercase: false, GloballyUnique: false);

    public const string AlphaNumeric = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public bool Matches(string name) =>
        name.Length >= MinLength && name.Length <= MaxLength &&
        name.All(c => AllowedChars.Contains(c) && (!Lowercase || char.IsLower(c) || char.IsAsciiDigit(c)));

    public char Separator => AllowedChars.Contains('-') ? '-' : AllowedChars.Contains('_') ? '_' : '\0';
}

/// <summary>
/// Deterministic resource-name suggestions: same inputs, same name - so a regenerated
/// design diffs to nothing in git. Globally-unique families carry a stable 6-hex entropy
/// segment derived from (workload, environment, type), never randomness.
/// </summary>
public static class NamingEngine
{
    /// <summary>Only families whose names are tenant-wide DNS-backed identifiers.</summary>
    private static readonly HashSet<string> KnownGloballyUnique = new(StringComparer.Ordinal)
    {
        AdsConstants.StorageAccount, AdsConstants.SqlServer, AdsConstants.CosmosDBAccount,
        AdsConstants.ContainerRegistry, AdsConstants.KeyVault, AdsConstants.AiSearch,
        AdsConstants.SignalR, AdsConstants.WebApp, AdsConstants.FunctionApp, AdsConstants.APIM,
    };

    private static readonly Dictionary<string, NamingProfile> Profiles = new(StringComparer.Ordinal)
    {
        [AdsConstants.StorageAccount] = new(3, 24, NamingProfile.AlphaNumeric, Lowercase: true, GloballyUnique: true),
        [AdsConstants.SqlServer] = new(3, 15, NamingProfile.AlphaNumeric, Lowercase: true, GloballyUnique: true),
        [AdsConstants.CosmosDBAccount] = new(3, 44, NamingProfile.AlphaNumeric + "-", Lowercase: true, GloballyUnique: true),
        [AdsConstants.ContainerRegistry] = new(5, 50, NamingProfile.AlphaNumeric, Lowercase: false, GloballyUnique: true),
        [AdsConstants.KeyVault] = new(3, 24, NamingProfile.AlphaNumeric + "-", Lowercase: false, GloballyUnique: true),
        [AdsConstants.AiSearch] = new(2, 64, NamingProfile.AlphaNumeric + "-", Lowercase: true, GloballyUnique: true),
        [AdsConstants.SignalR] = new(3, 63, NamingProfile.AlphaNumeric + "-", Lowercase: false, GloballyUnique: true),
        [AdsConstants.WebApp] = new(2, 60, NamingProfile.AlphaNumeric + "-", Lowercase: true, GloballyUnique: true),
        [AdsConstants.FunctionApp] = new(2, 60, NamingProfile.AlphaNumeric + "-", Lowercase: true, GloballyUnique: true),
        [AdsConstants.APIM] = new(1, 63, NamingProfile.AlphaNumeric + "-", Lowercase: true, GloballyUnique: true),
        [AdsConstants.AppConfiguration] = new(5, 64, NamingProfile.AlphaNumeric + "-_", Lowercase: false, GloballyUnique: false),
        [AdsConstants.EventHubs] = new(6, 50, NamingProfile.AlphaNumeric + "-", Lowercase: false, GloballyUnique: false),
        [AdsConstants.ServiceBus] = new(6, 50, NamingProfile.AlphaNumeric + "-", Lowercase: false, GloballyUnique: false),
        [AdsConstants.VirtualMachineScaleSet] = new(3, 63, NamingProfile.AlphaNumeric + "-", Lowercase: true, GloballyUnique: false),
    };

    public static NamingProfile ProfileFor(string stencilKey) => Profiles.GetValueOrDefault(stencilKey, NamingProfile.Generic);

    public static bool IsGloballyUnique(string stencilKey) => ProfileFor(stencilKey).GloballyUnique || KnownGloballyUnique.Contains(stencilKey);

    public static bool IsValid(string stencilKey, string name) => ProfileFor(stencilKey).Matches(name);

    /// <summary>workload-environment[-entropy] fitted to the profile; <paramref name="taken"/> resolved with stable counters.</summary>
    public static string Suggest(string stencilKey, string workload, string environment, IReadOnlyCollection<string>? taken = null)
    {
        var profile = ProfileFor(stencilKey);
        var sep = profile.Separator;
        var sepText = sep == '\0' ? "" : sep.ToString();

        var head = string.Join(sepText, new[] { Sanitize(workload, profile), Sanitize(environment, profile) }.Where(s => s.Length > 0).ToArray());
        var entropy = profile.GloballyUnique ? Entropy(stencilKey, workload, environment) : "";

        var candidate = Fit(head, entropy, extra: "", profile);
        var padding = Entropy(stencilKey, workload, environment + head).Where(c => profile.AllowedChars.Contains(c)).ToArray();
        while (candidate.Length < profile.MinLength && padding.Length > 0)
            candidate += padding[candidate.Length % padding.Length];

        for (var n = 2; taken is not null && taken.Contains(candidate, StringComparer.OrdinalIgnoreCase); n++)
            candidate = Fit(head, entropy, extra: sepText + n, profile);

        return candidate;
    }

    /// <summary>Workload side truncates first; entropy + counter are the tail and always survive.</summary>
    private static string Fit(string head, string entropy, string extra, NamingProfile profile)
    {
        var sepText = profile.Separator == '\0' ? "" : profile.Separator.ToString();
        var tail = entropy.Length + extra.Length == 0
            ? ""
            : (entropy.Length > 0 ? sepText : "") + entropy + extra;
        var budget = profile.MaxLength - tail.Length;
        if (budget <= 0) return tail.Substring(0, Math.Min(tail.Length, profile.MaxLength));
        var trimmed = head.Length <= budget ? head : head.Substring(0, budget).Trim(profile.Separator);
        return trimmed + tail;
    }

    private static string Sanitize(string text, NamingProfile profile)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            var c = profile.Lowercase ? char.ToLowerInvariant(raw) : raw;
            if (profile.AllowedChars.Contains(c))
            {
                if (c == profile.Separator && sb.Length > 0 && sb[^1] == c) continue; // collapse runs
                sb.Append(c);
            }
            else if (profile.Separator != '\0')
            {
                if (sb.Length > 0 && sb[^1] == profile.Separator) continue;
                sb.Append(profile.Separator);
            }
        }
        return sb.ToString().Trim(profile.Separator);
    }

    /// <summary>Stable, collision-resistant suffix - derived from the request, never from a RNG.</summary>
    private static string Entropy(string stencilKey, string workload, string environment) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{workload}|{environment}|{stencilKey}")))[..6].ToLowerInvariant();
}

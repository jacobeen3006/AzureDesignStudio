using System.Text.Json;
using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.ResourceGraph;

namespace AzureDesignStudio.Discovery.Naming;

public enum NameCheckOutcome { Available, Unavailable, NotCheckable, Error }

/// <summary>Outcome of one live check; <paramref name="Detail"/> names the reason for
/// Unavailable (provider reason/message) and for the non-available outcomes.</summary>
public sealed record NameCheckResult(NameCheckOutcome Outcome, string? Detail);

/// <summary>
/// Live name availability (v2 plan 10.4's online half): POSTs the ARM provider
/// <c>checkNameAvailability</c> endpoint for the eight globally-unique families whose REST
/// shapes are verified. Families without a verified shape - cosmos-db included, its
/// operation path could not be confirmed - report <see cref="NameCheckOutcome.NotCheckable"/>
/// with no HTTP call: guessing an endpoint is how a 404 gets misread as "available".
/// A response that carries no readable boolean is an <see cref="NameCheckOutcome.Error"/>,
/// never a fabricated Available.
/// </summary>
public sealed class NameAvailabilityClient(ArmTransport transport)
{
    // Keys are the AdsConstants stencil-key values; Discovery stays dependency-narrow
    // (no Core reference), and the offline tests pin the identity against AdsConstants.
    private sealed record FamilySpec(string Path, string ApiVersion, string? Type, string AvailabilityKey, bool RequiresLocation = false);

    private static readonly Dictionary<string, FamilySpec> Families = new(StringComparer.Ordinal)
    {
        ["storage-account"] = new("providers/Microsoft.Storage/checkNameAvailability", "2025-06-01", "Microsoft.Storage/storageAccounts", "nameAvailable"),
        ["sql-server"] = new("providers/Microsoft.Sql/checkNameAvailability", "2023-08-01-preview", "Microsoft.Sql/servers", "available"),
        ["key-vault"] = new("providers/Microsoft.KeyVault/checkNameAvailability", "2024-11-01", "Microsoft.KeyVault/vaults", "nameAvailable"),
        ["container-registry"] = new("providers/Microsoft.ContainerRegistry/checkNameAvailability", "2025-11-01", "Microsoft.ContainerRegistry/registries", "nameAvailable"),
        ["ai-search"] = new("providers/Microsoft.Search/checkNameAvailability", "2025-05-01", "searchServices", "nameAvailable"),
        // The SignalR check is scoped to a location - the URL names it, so the region is required.
        ["signalr"] = new("providers/Microsoft.SignalRService/locations/{0}/checkNameAvailability", "2024-03-01", "Microsoft.SignalRService/SignalR", "nameAvailable", RequiresLocation: true),
        // The Web path is lowercase checknameavailability on the wire - keep it verbatim.
        ["web-app"] = new("providers/Microsoft.Web/checknameavailability", "2024-11-01", "Microsoft.Web/sites", "nameAvailable"),
        ["function-app"] = new("providers/Microsoft.Web/checknameavailability", "2024-11-01", "Microsoft.Web/sites", "nameAvailable"),
        // ApiManagement's verified body carries only the name.
        ["apim"] = new("providers/Microsoft.ApiManagement/checkNameAvailability", "2024-05-01", null, "nameAvailable"),
    };

    /// <summary>The families this client can check live - exactly the verified table above.</summary>
    public static bool Supports(string stencilKey) => Families.ContainsKey(stencilKey);

    public static bool RequiresLocation(string stencilKey) =>
        Families.TryGetValue(stencilKey, out var family) && family.RequiresLocation;

    public async Task<NameCheckResult> CheckAsync(string stencilKey, string name, string subscriptionId,
        string? location, CancellationToken ct = default)
    {
        if (!Families.TryGetValue(stencilKey, out var family))
            return new NameCheckResult(NameCheckOutcome.NotCheckable, "No live availability check for this family");
        if (family.RequiresLocation && string.IsNullOrWhiteSpace(location))
            return new NameCheckResult(NameCheckOutcome.NotCheckable, "A region is required for this check");
        if (string.IsNullOrWhiteSpace(name))
            return new NameCheckResult(NameCheckOutcome.Error, "The name is empty - nothing to check.");

        var path = family.Path.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(family.Path, Uri.EscapeDataString(location!.Trim()))
            : family.Path;
        var url = $"{transport.Endpoints.ManagementEndpoint}/subscriptions/{subscriptionId}/{path}?api-version={family.ApiVersion}";
        var nameJson = JsonSerializer.Serialize(name.Trim());
        var body = family.Type is null
            ? $"{{\"name\":{nameJson}}}"
            : $"{{\"name\":{nameJson},\"type\":{JsonSerializer.Serialize(family.Type)}}}";
        try
        {
            var json = await transport.PostAsync(url, body, ct).ConfigureAwait(false);
            return Parse(json, family.AvailabilityKey);
        }
        catch (ResourceGraphException ex)
        {
            // ARM's own refusal: name the status and code, never echo the body.
            return new NameCheckResult(NameCheckOutcome.Error, $"HTTP {(int)ex.StatusCode}: {ex.Code}");
        }
        catch (Exception e)
        {
            return new NameCheckResult(NameCheckOutcome.Error, e.Message);
        }
    }

    private static NameCheckResult Parse(string json, string availabilityKey)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryGetBool(root, availabilityKey, out var available))
                return new NameCheckResult(NameCheckOutcome.Error, "the availability response carried no readable availability flag");
            if (available)
                return new NameCheckResult(NameCheckOutcome.Available, null);
            // Absent-tolerant: reason and message both optional, composed when present.
            var detail = string.Join(" - ", new[] { TryGetString(root, "reason"), TryGetString(root, "message") }
                .Where(s => !string.IsNullOrEmpty(s)));
            return new NameCheckResult(NameCheckOutcome.Unavailable, detail.Length > 0 ? detail : null);
        }
        catch (JsonException)
        {
            return new NameCheckResult(NameCheckOutcome.Error, "the availability response is not valid JSON");
        }
    }

    private static bool TryGetBool(JsonElement root, string key, out bool value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = property.Value.GetBoolean();
                return true;
            }
        }
        value = false;
        return false;
    }

    private static string? TryGetString(JsonElement root, string key)
    {
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        return null;
    }
}

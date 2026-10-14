using System.Text.Json;

namespace AzureDesignStudio.Discovery.ChangeFeed;

/// <summary>
/// One control-plane change of a resource (v2 plan 9.1), normalized from the ARG
/// <c>resourcechanges</c> table. "Unspecified"/"Unknown" placeholders are kept as
/// <c>null</c> - the UI shows "unknown actor" uniformly instead of two service strings.
/// </summary>
public sealed record ChangeFeedEntry(
    string ChangeKey,
    DateTimeOffset ChangeTime,
    string TargetResourceId,
    string? TargetResourceType,
    string ChangeType,
    string? CorrelationId,
    string? ChangedBy,
    string? ChangedByType,
    string? ClientType,
    string? Operation,
    int ChangesCount,
    JsonElement? ChangedProperties);

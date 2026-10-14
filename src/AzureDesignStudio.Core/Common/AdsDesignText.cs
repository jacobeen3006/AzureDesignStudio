using System.Text.Json;
using System.Text.Json.Nodes;

namespace AzureDesignStudio.Core.Common;

/// <summary>A parsed <c>.ads.json</c> design document (v2 plan 10.1).</summary>
/// <param name="Version">Format version; see <see cref="AdsDesignText.Version"/>.</param>
/// <param name="Name">Optional design name carried inside the file.</param>
/// <param name="Graph">The design graph; node/group entities arrive as <c>JsonElement</c> (TypeKey dispatch happens on canvas load).</param>
public sealed record AdsDesignFile(int Version, string? Name, AzureDesignStudio.Core.DTO.DiagramGraph Graph);

/// <summary>Malformed design text with the failure localized - never a raw parser leak.</summary>
public sealed class AdsDesignFormatException : FormatException
{
    public AdsDesignFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Text-first <c>.ads.json</c> (v2 plan 10.1): deterministic, git-diff-friendly design text.
/// Entities keep their full JSON payload (TypeKey discriminator intact, lossless against the
/// live DTO set); sections are sorted by identity so re-saving an unchanged design produces
/// byte-identical text regardless of canvas insertion order. Same bytes in, same bytes out -
/// the invariant that makes designs reviewable in git (§C) and diffable across versions.
/// Property casing matches the existing saved-design format (PascalCase, enums numeric).
/// </summary>
public static class AdsDesignText
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Serialize a design graph to canonical <c>.ads.json</c> text.</summary>
    public static string Write(AzureDesignStudio.Core.DTO.DiagramGraph graph, string? name = null) =>
        Write(new AdsDesignFile(Version, name, graph));

    public static string Write(AdsDesignFile file)
    {
        if (file.Version != Version)
            throw new AdsDesignFormatException($"Cannot write format version {file.Version}; this build writes version {Version}.");

        var root = new JsonObject { ["version"] = Version };
        if (file.Name is { Length: > 0 }) root["name"] = file.Name;
        root["groups"] = SortedEntities(file.Graph.Groups, "groups");
        root["nodes"] = SortedEntities(file.Graph.Nodes, "nodes");
        root["links"] = SortedLinks(file.Graph.Links);
        return root.ToJsonString(Indented);
    }

    /// <summary>Parse <c>.ads.json</c> text; shape violations surface as <see cref="AdsDesignFormatException"/>.</summary>
    public static AdsDesignFile Read(string text)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException e)
        {
            throw new AdsDesignFormatException("The design text is not valid JSON.", e);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new AdsDesignFormatException($"Design root must be an object, found {root.ValueKind}.");
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != Version)
                throw new AdsDesignFormatException($"Unsupported or missing design format version; expected {Version}.");

            var graph = new AzureDesignStudio.Core.DTO.DiagramGraph();
            foreach (var element in Enumerate(root, "groups")) graph.Groups.Add(element);
            foreach (var element in Enumerate(root, "nodes")) graph.Nodes.Add(element);

            var links = root.TryGetProperty("links", out var l) && l.ValueKind == JsonValueKind.Array ? l : default;
            if (links.ValueKind == JsonValueKind.Array)
                foreach (var element in links.EnumerateArray())
                    graph.Links.Add(DeserializeLink(element));

            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            return new AdsDesignFile(Version, name, graph);
        }
    }

    private static IEnumerable<JsonElement> Enumerate(JsonElement root, string section)
    {
        if (!root.TryGetProperty(section, out var arr)) yield break;
        if (arr.ValueKind != JsonValueKind.Array)
            throw new AdsDesignFormatException($"Design section \"{section}\" must be an array, found {arr.ValueKind}.");
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new AdsDesignFormatException($"Design entity in \"{section}\" must be an object, found {item.ValueKind}.");
            yield return item.Clone();
        }
    }

    private static AzureDesignStudio.Core.DTO.LinkModelDto DeserializeLink(JsonElement element)
    {
        try
        {
            return JsonSerializer.Deserialize<AzureDesignStudio.Core.DTO.LinkModelDto>(element)
                ?? throw new AdsDesignFormatException("Link entity deserialized to null.");
        }
        catch (JsonException e)
        {
            throw new AdsDesignFormatException($"Invalid link entity: {e.Message}", e);
        }
    }

    private static JsonArray SortedEntities(IList<object> entities, string section)
    {
        var array = new JsonArray();
        foreach (var entity in entities
                     .Select(e => (Node: ToNode(e, section), Id: IdOf(e)))
                     .OrderBy(x => x.Id, StringComparer.Ordinal)
                     .ThenBy(x => x.Node.ToJsonString(), StringComparer.Ordinal)) // full-text tiebreak keeps equal ids deterministic
            array.Add(entity.Node);
        return array;
    }

    private static JsonArray SortedLinks(IList<AzureDesignStudio.Core.DTO.LinkModelDto> links)
    {
        var array = new JsonArray();
        foreach (var node in links
                     .Select(l => JsonSerializer.SerializeToNode(l)!) // typed DTO never serializes to null
                     .OrderBy(n => Key(n, "SourcePortParentId"), StringComparer.Ordinal)
                     .ThenBy(n => Key(n, "TargetPortParentId"), StringComparer.Ordinal)
                     .ThenBy(n => n.ToJsonString(), StringComparer.Ordinal))
            array.Add(node);
        return array;
    }

    private static JsonNode ToNode(object entity, string section)
    {
        try
        {
            // Typed DTOs serialize by runtime type; entities already read from text are JsonElement and pass through.
            return (entity is JsonElement je ? JsonSerializer.SerializeToNode(je) : JsonSerializer.SerializeToNode(entity, entity.GetType()))
                ?? throw new AdsDesignFormatException($"Entity in \"{section}\" serialized to null.");
        }
        catch (NotSupportedException e)
        {
            throw new AdsDesignFormatException($"Entity in \"{section}\" cannot be serialized: {e.Message}", e);
        }
    }

    private static string IdOf(object entity) => entity switch
    {
        AzureDesignStudio.Core.DTO.AzureNodeDto dto => dto.Id ?? string.Empty,
        JsonElement { ValueKind: JsonValueKind.Object } je
            when je.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String => id.GetString()!,
        _ => string.Empty, // unidentifiable entities sort first, then by text - still deterministic
    };

    private static string Key(JsonNode node, string property) =>
        node is JsonObject o && o.TryGetPropertyValue(property, out var v) && v is JsonValue s && s.TryGetValue<string>(out var text) ? text : string.Empty;
}

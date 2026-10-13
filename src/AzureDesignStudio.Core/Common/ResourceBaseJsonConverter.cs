using AzureDesignStudio.AzureResources.Base;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureDesignStudio.Core.Common
{
    public class ResourceBaseJsonConverter : JsonConverter<ResourceBase>
    {
        /// <summary>
        /// ARM resource type string ("Microsoft.Network/virtualNetworks/virtualNetworkPeerings")
        /// to CLR model, discovered once from the generated AzureResources assembly.
        /// </summary>
        private static readonly Lazy<Dictionary<string, Type>> TypeRegistry = new(() =>
        {
            var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in typeof(ResourceBase).Assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(ResourceBase).IsAssignableFrom(type))
                    continue;
                try
                {
                    if (Activator.CreateInstance(type) is ResourceBase probe && !string.IsNullOrEmpty(probe.Type))
                        map.TryAdd(probe.Type, type);
                }
                catch (Exception e) when (e is MissingMethodException or NotImplementedException)
                {
                    // No parameterless ctor, or a Type getter that isn't implemented:
                    // such types can never be a deserialization target.
                }
            }
            return map;
        });

        public override ResourceBase? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp) || typeProp.GetString() is not { } armType)
                throw new JsonException("ARM resource JSON is missing the required 'type' property.");

            if (!TypeRegistry.Value.TryGetValue(armType, out var clrType))
                throw new JsonException($"Unknown ARM resource type '{armType}'.");

            return JsonSerializer.Deserialize(doc.RootElement.GetRawText(), clrType, options) as ResourceBase
                ?? throw new JsonException($"Deserialization of ARM resource type '{armType}' produced no resource.");
        }

        public override void Write(Utf8JsonWriter writer, ResourceBase value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}

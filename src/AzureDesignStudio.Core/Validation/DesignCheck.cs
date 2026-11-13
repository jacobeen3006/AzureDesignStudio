// Licensed under the MIT License. See LICENSE in the project root for license information.
using System.Text.Json;
using AutoMapper;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Models;

namespace AzureDesignStudio.Core.Validation
{
    /// <summary>Checks a stored design payload without a canvas: validation issues plus lint findings.</summary>
    public static class DesignCheck
    {
        private static readonly Lazy<IMapper> Mapper =
            new(() => new MapperConfiguration(cfg => cfg.AddProfile<AzureNodeProfile>()).CreateMapper());

        /// <exception cref="InvalidDataException">The payload is not a design graph or cannot be loaded.</exception>
        public static ValidationReport Run(string designDataJson)
        {
            DiagramGraph? graph;
            try
            {
                // The canvas load path deserializes the saved payload the same way (default options).
                graph = JsonSerializer.Deserialize<DiagramGraph>(designDataJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"design payload is not valid design JSON: {ex.Message}", ex);
            }

            if (graph is null)
                throw new InvalidDataException("design payload is not a design graph");

            var diagram = new BlazorDiagram();
            diagram.Options.Links.Factory = DataModelFactory.LinkModelFactory;
            var detached = new List<string>();
            try
            {
                DataModelFactory.LoadDiagramFromDto(diagram, graph, Mapper.Value, detached: detached);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidDataException($"design payload could not be loaded: {ex.Message}", ex);
            }

            // A group this build does not know loads as an unknown node and is dropped, its children
            // re-rooted; linting what is left would report on a different design.
            var droppedGroups = graph.Groups.Count - diagram.Groups.Count;
            var droppedNodes = graph.Nodes.Count - diagram.Nodes.Count;
            if (droppedGroups != 0 || droppedNodes != 0 || detached.Count > 0)
            {
                var unknownTypes = graph.Groups.Concat(graph.Nodes).Select(TypeKeyOf)
                    .Where(k => k is not null && DataModelFactory.CreateNodeModelFromKey(k, "probe", string.Empty) is UnknownNodeModel)
                    .Distinct().ToList();
                throw new InvalidDataException(
                    $"design payload did not load completely: {Math.Max(droppedGroups, 0)} group(s) and {Math.Max(droppedNodes, 0)} node(s) dropped, {detached.Count} detached"
                    + (unknownTypes.Count > 0 ? $"; unknown type keys: {string.Join(", ", unknownTypes)}" : string.Empty));
            }

            return ValidationReport.For(diagram.Groups.Cast<NodeModel>().Concat(diagram.Nodes).ToList());
        }

        private static string? TypeKeyOf(object dto) => dto switch
        {
            AzureNodeDto d => d.TypeKey,
            JsonElement je when je.ValueKind == JsonValueKind.Object && je.TryGetProperty("TypeKey", out var k) => k.GetString(),
            _ => null,
        };
    }
}

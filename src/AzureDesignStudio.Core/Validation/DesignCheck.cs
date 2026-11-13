// Licensed under the MIT License. See LICENSE in the project root for license information.
using System.Text.Json;
using AutoMapper;
using AzureDesignStudio.Core.DTO;
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
            try
            {
                DataModelFactory.LoadDiagramFromDto(diagram, graph, Mapper.Value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidDataException($"design payload could not be loaded: {ex.Message}", ex);
            }

            return ValidationReport.For(diagram.Groups.Cast<NodeModel>().Concat(diagram.Nodes).ToList());
        }
    }
}

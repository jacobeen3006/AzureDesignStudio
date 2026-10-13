using AzureDesignStudio.Core;
using AzureDesignStudio.Models;
using Blazor.Diagrams.Core.Options;
using Blazor.Diagrams.Core.PathGenerators;
using Blazor.Diagrams.Core.Routers;
using Blazor.Diagrams.Options;
using System.Net.Http.Json;
namespace AzureDesignStudio.Services
{
    public class AdsContext
    {
        private readonly IHttpClientFactory clientFactory = null!;
        public AdsContext(IHttpClientFactory factory)
        {
            clientFactory = factory;
        }

        public async Task InitializeAsync()
        {
            if (AllStencils == null)
            {
                var httpClient = clientFactory.CreateClient("AzureDesignStudio.Root");
                AllStencils = (await httpClient.GetFromJsonAsync<IReadOnlyList<StencilModel>>("ads-stencils.json"))!
                    .OrderBy(s => s.Label).ToList();
            }
        }
        public AdsDiagram Diagram { get; set; } = new AdsDiagram(CreateDiagramOptions());
        private static BlazorDiagramOptions CreateDiagramOptions()
        {
            var options = new BlazorDiagramOptions
            {
                AllowMultiSelection = true, // Whether to allow multi selection using CTRL
                LinksLayerOrder = 1, // Links on top of nodes
                GridSize = 10, // Snap to a 10px grid
            };
            options.Groups.Enabled = false;
            options.Links.DefaultRouter = new OrthogonalRouter();
            options.Links.DefaultPathGenerator = new StraightPathGenerator();
            options.Links.Factory = DataModelFactory.LinkModelFactory;
            options.Zoom.Minimum = 0.5; // Minimum zoom value
            options.Zoom.Inverse = false; // Whether to inverse the direction of the zoom when using the wheel
            return options;
        }
        public string DraggedStencilKey { get; set; } = string.Empty;
        public string CurrentDesignName { get; set; } = null!;

        public IReadOnlyList<StencilModel> AllStencils = null!;
    }
}

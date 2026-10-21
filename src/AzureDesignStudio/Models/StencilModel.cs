namespace AzureDesignStudio.Models
{
    public class StencilModel
    {
        public string Key { get; set; } = default!;
        public string Name { get; set; } = default!;
        public string IconPath { get; set; } = default!;
        public string Label { get; set; } = default!;
        public string? ReferenceArchPath { get; set; } = null;
        public StencilCategory Category { get; set; }
    }

    public enum StencilCategory
    {
        Networking = 0,
        Compute,
        Database,
        Storage,
        /// <summary>Cross-cutting resources such as resource groups.</summary>
        General,
        Gallery,
        Containers,
        Integration,
        SecurityAndOperations,
        AiAndAnalytics
    }

    public record StencilPanelModel
    {
        public string Key { get; set; } = default!;
        public string Name { get; set; } = default!;
        public string IconPath { get; set; } = default!;
        public StencilCategory Category { get; set; }
    }
}

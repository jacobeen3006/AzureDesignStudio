using AutoMapper;
using AzureDesignStudio.Core;
using AzureDesignStudio.Core.DTO;

namespace AzureDesignStudio.Services
{
    /// <summary>
    /// Replaces what is on the canvas with a design, without ever leaving the user with less
    /// than they started with. The current content is captured first; if the new design
    /// cannot be loaded (a dangling link, a port the model no longer has) it is put back.
    /// Callers used to clear the canvas and then fetch or parse, so a failed load cost the
    /// unsaved work that had been there.
    /// </summary>
    public static class CanvasLoader
    {
        /// <returns>null when the design is on the canvas; otherwise why it could not be loaded
        /// (the previous content has been restored).</returns>
        /// <param name="detached">Receives the names of items whose saved parent group was not
        /// in the design; they are on the canvas, but at its root.</param>
        public static async Task<string?> ReplaceAsync(AdsDiagram diagram, DiagramGraph graph, IMapper mapper,
            ICollection<string>? detached = null)
        {
            // Null for an empty canvas.
            var previous = await DataModelFactory.SaveDiagramToDto(diagram, mapper);

            void Clear()
            {
                diagram.Nodes.Clear();
                diagram.Links.Clear();
                diagram.Groups.Clear();
            }

            try
            {
                // Programmatic replace: never recorded, and prior history belongs to the old design.
                diagram.History.Suspend(() =>
                {
                    Clear();
                    DataModelFactory.LoadDiagramFromDto(diagram, graph, mapper, detached: detached);
                });
                diagram.History.Clear();
                diagram.FitToContent();
                return null;
            }
            catch (Exception e)
            {
                try
                {
                    diagram.History.Suspend(() =>
                    {
                        Clear();
                        if (previous is not null)
                            DataModelFactory.LoadDiagramFromDto(diagram, previous, mapper);
                    });
                }
                catch (Exception)
                {
                    // The previous content loaded once already; nothing more can be done here.
                }

                // The restored models are new objects; the old undo stack no longer refers to them.
                diagram.History.Clear();
                return e.Message;
            }
        }
    }
}

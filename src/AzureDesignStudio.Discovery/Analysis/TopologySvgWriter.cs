using System.Globalization;
using System.Net;
using System.Text;
using AzureDesignStudio.Discovery.Inventory;

namespace AzureDesignStudio.Discovery.Analysis;

/// <summary>
/// Static topology SVG (v2 plan 9.6 closer): the same derived graph and canonical framing
/// the interactive TopologyView draws, rendered as a self-contained document the report
/// bundle can ship. No scripts, no external assets, no event handlers - everything the
/// SafeSvg gate in the bundle refuses is never produced here. Deterministic: the layout
/// engine sorts by ARM id, so the same items always produce the same bytes.
/// </summary>
public static class TopologySvgWriter
{
    private const double LegendH = 34;

    /// <summary>Auto-layout SVG of a snapshot set; null when there is nothing to draw.</summary>
    public static string? Render(IReadOnlyList<InventoryItem> items)
    {
        if (items.Count == 0) return null;
        var graph = TopologyDeriver.Derive(items);
        if (graph.Nodes.Count == 0) return null;
        var layout = TopologyLayout.Layout(graph);

        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" font-family=\"Segoe UI, Arial, sans-serif\"")
          .Append(" width=\"").Append(N(layout.Width)).Append("\" height=\"").Append(N(layout.Height + LegendH))
          .Append("\" viewBox=\"0 0 ").Append(N(layout.Width)).Append(' ').Append(N(layout.Height + LegendH)).Append("\">\n");
        sb.Append("<rect width=\"").Append(N(layout.Width)).Append("\" height=\"").Append(N(layout.Height + LegendH))
          .Append("\" fill=\"#fbfbfd\"/>\n");

        foreach (var c in layout.Containers)
        {
            sb.Append("<rect x=\"").Append(N(c.X)).Append("\" y=\"").Append(N(c.Y))
              .Append("\" width=\"").Append(N(c.W)).Append("\" height=\"").Append(N(c.H))
              .Append("\" rx=\"10\" fill=\"").Append(TopologyLayout.FrameFill(c.Id))
              .Append("\" stroke=\"").Append(TopologyLayout.FrameStroke(c.Id))
              .Append("\" stroke-width=\"1.5\"");
            if (TopologyLayout.IsLane(c.Id)) sb.Append(" stroke-dasharray=\"6 4\"");
            sb.Append(" opacity=\"0.9\"/>\n");
            sb.Append("<text x=\"").Append(N(c.X + 10)).Append("\" y=\"").Append(N(c.Y + 20))
              .Append("\" font-size=\"13\" font-weight=\"600\" fill=\"#555\">")
              .Append(Esc(Short(c.Label, 46))).Append("</text>\n");
        }

        var byId = new Dictionary<string, PlacedNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in layout.Nodes) byId[n.Node.Id] = n;

        foreach (var e in graph.Edges)
        {
            if (!byId.TryGetValue(e.From, out var a) || !byId.TryGetValue(e.To, out var b)) continue;
            sb.Append("<line x1=\"").Append(N(a.X + TopologyLayout.NodeW / 2)).Append("\" y1=\"").Append(N(a.Y + TopologyLayout.NodeH / 2))
              .Append("\" x2=\"").Append(N(b.X + TopologyLayout.NodeW / 2)).Append("\" y2=\"").Append(N(b.Y + TopologyLayout.NodeH / 2))
              .Append("\" stroke=\"").Append(TopologyLayout.EdgeColor(e.Kind)).Append("\" stroke-width=\"1.4\" opacity=\"0.75\"/>\n");
        }

        foreach (var n in layout.Nodes)
        {
            sb.Append("<rect x=\"").Append(N(n.X)).Append("\" y=\"").Append(N(n.Y))
              .Append("\" width=\"").Append(N(n.W)).Append("\" height=\"").Append(N(n.H))
              .Append("\" rx=\"7\" fill=\"").Append(n.Node.External ? "#f5f5f5" : "#ffffff")
              .Append("\" stroke=\"#d9d9d9\" stroke-width=\"1.4\"");
            if (n.Node.External) sb.Append(" stroke-dasharray=\"5 3\"");
            sb.Append("/>\n");
            sb.Append("<text x=\"").Append(N(n.X + 10)).Append("\" y=\"").Append(N(n.Y + 22))
              .Append("\" font-size=\"12.5\" font-weight=\"600\" fill=\"#222\">").Append(Esc(Short(n.Node.Name, 24))).Append("</text>\n");
            sb.Append("<text x=\"").Append(N(n.X + 10)).Append("\" y=\"").Append(N(n.Y + 40))
              .Append("\" font-size=\"10.5\" fill=\"#888\">").Append(Esc(Short(n.Node.Type, 30))).Append("</text>\n");
        }

        // Legend: one swatch per edge kind actually present, in a fixed order - a reader
        // of a static file has no hover to ask what a line color means.
        var kinds = new List<TopologyEdgeKind>();
        foreach (TopologyEdgeKind k in Enum.GetValues<TopologyEdgeKind>())
            if (graph.Edges.Any(e => e.Kind == k)) kinds.Add(k);
        if (kinds.Count > 0)
        {
            double x = 12;
            var y = layout.Height + 20;
            foreach (var k in kinds)
            {
                sb.Append("<rect x=\"").Append(N(x)).Append("\" y=\"").Append(N(y - 9))
                  .Append("\" width=\"10\" height=\"10\" fill=\"").Append(TopologyLayout.EdgeColor(k)).Append("\"/>\n");
                var label = TopologyLayout.KindWord(k);
                sb.Append("<text x=\"").Append(N(x + 14)).Append("\" y=\"").Append(N(y))
                  .Append("\" font-size=\"11\" fill=\"#666\">").Append(Esc(label)).Append("</text>\n");
                x += 26 + label.Length * 6.5;
            }
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Esc(string s) => WebUtility.HtmlEncode(s);

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

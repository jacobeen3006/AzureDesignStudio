using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureDesignStudio.Discovery.Report;

/// <summary>
/// Static bundle assembly (v2 plan 9.6): filename -> content for `report.json`
/// (machine-readable), `report.html` (self-contained) and `controls-audit.csv`
/// (framework-mapped). Deterministic: same model, same bytes, no clocks inside.
/// The desktop host writes these to the folder the user picks; PNG/raster export
/// rides on the topology SVG the 8.x drawer produces (embedded when present).
/// </summary>
public static class ReportBundle
{
    public const string JsonFile = "report.json";
    public const string HtmlFile = "report.html";
    public const string CsvFile = "controls-audit.csv";
    public const string SvgFile = "topology.svg";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static IReadOnlyDictionary<string, string> Build(ReportModel model)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JsonFile] = JsonSerializer.Serialize(model, JsonOptions),
            [HtmlFile] = ReportHtml.Render(model),
            [CsvFile] = ControlsCsv(model),
        };

        if (SafeSvg(model.TopologySvg))
            files[SvgFile] = model.TopologySvg!;

        return files;
    }

    /// <summary>SVG arrives from the topology writer; refuse anything script-shaped in a shareable file.</summary>
    internal static bool SafeSvg(string? svg) =>
        svg is { Length: > 0 } &&
        !svg.Contains("<script", StringComparison.OrdinalIgnoreCase) &&
        !svg.Contains("<iframe", StringComparison.OrdinalIgnoreCase) &&
        !svg.Contains("javascript:", StringComparison.OrdinalIgnoreCase);

    /// <summary>One row per (finding, framework control); severity and human detail ride along for audit filters.</summary>
    public static string ControlsCsv(ReportModel model)
    {
        var sb = new StringBuilder("kind,resource_id,severity,framework,control,title,detail\n");

        if (model.Exposure is { } exposure)
            foreach (var f in exposure)
                foreach (var c in ControlsMap.For(f.Kind))
                    Csv(sb, f.Kind, f.ResourceId, f.Severity.ToString(), c, f.Detail);

        if (model.Unmanaged is { } audit)
        {
            foreach (var f in audit.Unmanaged)
                foreach (var c in ControlsMap.For("unmanaged"))
                    Csv(sb, "unmanaged", f.Id, "", c, f.Reason);
            foreach (var f in audit.ProtectedResources)
                foreach (var c in ControlsMap.For("protected"))
                    Csv(sb, "protected", f.Id, "", c, f.Reason);
        }

        if (model.Drift is { } drift)
            foreach (var d in drift)
                foreach (var c in ControlsMap.For("drift-" + d.Status))
                    Csv(sb, "drift-" + d.Status, d.ResourceId, "", c, d.Operation ?? "");

        return sb.ToString();
    }

    private static void Csv(StringBuilder sb, string kind, string resourceId, string severity, ControlMapping c, string detail) =>
        sb.Append(Cell(kind)).Append(',').Append(Cell(resourceId)).Append(',').Append(Cell(severity))
            .Append(',').Append(Cell(c.Framework)).Append(',').Append(Cell(c.Control))
            .Append(',').Append(Cell(c.Title)).Append(',').Append(Cell(detail)).Append('\n');

    private static string Cell(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}

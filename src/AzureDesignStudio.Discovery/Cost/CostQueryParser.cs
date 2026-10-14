using System.Text.Json;

namespace AzureDesignStudio.Discovery.Cost;

/// <summary>
/// Cost Management query response -> typeless-agnostic spend rows (v2 plan 9.5).
/// Columns are located by name, not position: the API reorders and adds columns
/// between versions, and grouping columns vanish entirely for empty dimensions.
/// </summary>
public static class CostQueryParser
{
    /// <summary>Parses a query response envelope (`properties.columns` + `properties.rows`).</summary>
    public static IReadOnlyList<SpendRow> Parse(JsonElement response)
    {
        if (!response.TryGetProperty("properties", out var props)
            || !props.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array
            || !props.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return [];

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var col = 0;
        foreach (var c in columns.EnumerateArray())
            if (c.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                index[name.GetString()!] = col++;

        var costIdx = index.TryGetValue("Cost", out var i) ? i
            : index.TryGetValue("TotalCost", out i) ? i
            : -1;
        if (costIdx < 0) return [];

        var result = new List<SpendRow>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array) continue;
            var cells = row.EnumerateArray().ToArray();
            if (cells.Length <= costIdx) continue;

            result.Add(new SpendRow(
                Text(cells, index, "ResourceId"),
                Text(cells, index, "ResourceGroupName"),
                Text(cells, index, "ServiceFamily"),
                Text(cells, index, "MeterCategory"),
                Text(cells, index, "ResourceLocation"),
                Money(cells[costIdx]),
                Text(cells, index, "Currency") ?? ""));
        }

        return result;
    }

    /// <summary>Rows + window + scope -> result with provenance; currency is the one the response carried.</summary>
    public static ActualSpendResult ToResult(JsonElement response, string scope, string region,
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset asOf)
    {
        var rows = Parse(response);
        var currency = "";
        foreach (var r in rows)
            if (r.Currency.Length > 0) { currency = r.Currency; break; }

        return new ActualSpendResult(rows,
            new SpendProvenance("cost-management-query", scope, region, currency, from, to, asOf));
    }

    private static string? Text(JsonElement[] cells, Dictionary<string, int> index, string column) =>
        index.TryGetValue(column, out var i) && i < cells.Length && cells[i].ValueKind == JsonValueKind.String
            ? cells[i].GetString() is { Length: > 0 } s ? s : null
            : null;

    private static decimal Money(JsonElement cell) => cell.ValueKind switch
    {
        JsonValueKind.Number => cell.TryGetDecimal(out var d) ? d : (decimal)cell.GetDouble(),
        // Aggregation cells occasionally arrive quoted; treat as data, not error.
        JsonValueKind.String when decimal.TryParse(cell.GetString(), out var s) => s,
        _ => 0m,
    };
}

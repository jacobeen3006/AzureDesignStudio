namespace AzureDesignStudio.Discovery.Cost;

/// <summary>Cost by dimension (v2 plan 9.5): sums per bucket, biggest first, deterministic on ties.</summary>
public static class CostRollup
{
    public static IReadOnlyList<SpendBucket> By(IReadOnlyList<SpendRow> rows, SpendDimension dimension)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = Key(row, dimension);
            if (key is null) continue; // rows without this dimension stay unattributed, never in a bucket
            totals[key] = totals.TryGetValue(key, out var t) ? t + row.Cost : row.Cost;
        }

        var sum = 0m;
        foreach (var t in totals.Values) sum += t;

        var buckets = new List<SpendBucket>(totals.Count);
        foreach (var (key, cost) in totals)
            buckets.Add(new SpendBucket(key, cost, sum == 0m ? 0d : (double)(cost / sum)));

        buckets.Sort((a, b) =>
        {
            var c = b.Cost.CompareTo(a.Cost);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });
        return buckets;
    }

    public static decimal Total(IReadOnlyList<SpendRow> rows)
    {
        var sum = 0m;
        foreach (var r in rows) sum += r.Cost;
        return sum;
    }

    private static string? Key(SpendRow row, SpendDimension dimension) => dimension switch
    {
        SpendDimension.Resource => row.ResourceId,
        SpendDimension.ResourceGroup => row.ResourceGroup,
        SpendDimension.ServiceFamily => row.ServiceFamily,
        SpendDimension.MeterCategory => row.MeterCategory,
        SpendDimension.Location => row.Location,
        _ => null,
    };
}

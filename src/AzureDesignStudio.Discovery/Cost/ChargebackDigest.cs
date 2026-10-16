using System.Globalization;
using System.Text;
using AzureDesignStudio.Discovery.Inventory;

namespace AzureDesignStudio.Discovery.Cost;

/// <summary>One owner pile [77]: what the tagged (or untagged) resources cost in one currency.</summary>
/// <param name="Share">This pile's share (0..1) of the GRAND total of its own currency -
/// owner piles, `untagged` and `unmatched-spend` together. Never blended across currencies.</param>
public sealed record ChargebackRow(string Owner, string Currency, decimal Cost, int ResourceCount, double Share);

/// <summary>
/// [77] Chargeback/showback digest: join the Cost Management rows to the scan's own inventory
/// by resource id, read the owner tag, and pile the spend per owner - with the two honest
/// leftovers spelled out: spend on resources this scan never saw (`unmatched-spend`) and spend
/// on resources nobody owns (`untagged`). The untagged pile is the number a chargeback must
/// never hide, so it is a first-class row, never dropped.
/// </summary>
public static class ChargebackDigest
{
    /// <summary>Owner tag keys tried in order; the first non-empty value wins.</summary>
    public static readonly IReadOnlyList<string> OwnerTagKeys = ["owner", "cost-center", "costcenter", "team"];

    public const string UntaggedOwner = "untagged";
    public const string UnmatchedOwner = "unmatched-spend";

    public static IReadOnlyList<ChargebackRow> Build(IReadOnlyList<SpendRow> rows, IReadOnlyList<InventoryItem> inventory)
    {
        var byId = new Dictionary<string, InventoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in inventory) byId[item.Id.Trim()] = item;

        // currency -> owner -> (cost, resources)
        var piles = new Dictionary<string, Dictionary<string, (decimal Cost, int Count)>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var currency = row.Currency.Length == 0 ? "n/a" : row.Currency;
            string owner;
            if (row.ResourceId is not { Length: > 0 } rid || !byId.TryGetValue(rid.Trim(), out var item))
                owner = UnmatchedOwner;
            else
            {
                var value = OwnerOf(item);
                owner = value is { Length: > 0 } v ? v : UntaggedOwner;
            }

            if (!piles.TryGetValue(currency, out var owners))
                piles[currency] = owners = new Dictionary<string, (decimal, int)>(StringComparer.OrdinalIgnoreCase);
            owners.TryGetValue(owner, out var cur);
            owners[owner] = (cur.Cost + row.Cost, cur.Count + 1);
        }

        var result = new List<ChargebackRow>();
        foreach (var (currency, owners) in piles)
        {
            var total = owners.Values.Sum(o => o.Cost);
            // Unmatched rows share one pile across differently-tagged resources; counting rows,
            // not resources, keeps that pile honest about what it actually holds.
            foreach (var (owner, (cost, count)) in owners)
                result.Add(new ChargebackRow(owner, currency, cost, count,
                    total == 0 ? 0 : (double)(cost / total)));
        }

        return result
            .OrderBy(r => r.Currency, StringComparer.Ordinal).ThenByDescending(r => r.Cost)
            .ThenBy(r => r.Owner, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Flat CSV digest: piles, then a footer naming the source, window and the tag keys,
    /// so a spreadsheet alone still carries its provenance.</summary>
    public static string Csv(IReadOnlyList<ChargebackRow> rows, SpendProvenance provenance)
    {
        var sb = new StringBuilder("owner,currency,cost,share,resources\n");
        foreach (var r in rows)
            sb.Append(CsvCell(r.Owner)).Append(',')
              .Append(CsvCell(r.Currency)).Append(',')
              .Append(r.Cost.ToString("0.00####", CultureInfo.InvariantCulture)).Append(',')
              .Append((r.Share * 100).ToString("0.0", CultureInfo.InvariantCulture)).Append("%,")
              .Append(r.ResourceCount.ToString(CultureInfo.InvariantCulture)).Append('\n');

        sb.Append("# unmatched-spend = cost rows whose resource this scan never saw (no id or outside the scope); untagged = matched resources with no owner tag\n");
        sb.Append("# owner is the first matching tag key: ").Append(string.Join(" > ", OwnerTagKeys)).Append('\n');
        sb.Append("# source: ").Append(provenance.Source).Append(" · scope ").Append(provenance.Scope)
          .Append(" · endpoint ").Append(provenance.Region)
          .Append(" · window ").Append(provenance.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
          .Append(" - ").Append(provenance.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
          .Append(" · fetched ").Append(provenance.AsOf.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("Z\n");
        return sb.ToString();
    }

    private static string? OwnerOf(InventoryItem item)
    {
        foreach (var key in OwnerTagKeys)
            foreach (var (k, v) in item.Tags)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && v.Trim().Length > 0)
                    return v.Trim();
        return null;
    }

    private static string CsvCell(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}

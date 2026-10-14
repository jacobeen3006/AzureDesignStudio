namespace AzureDesignStudio.Discovery.Cost;

/// <summary>Where a spend figure came from - plan 9.5: every $ carries region/currency/source/as-of.</summary>
/// <param name="Source">Query engine that produced the numbers (e.g. `cost-management-query`).</param>
/// <param name="Scope">ARM scope the query ran against (`subscriptions/...` form).</param>
/// <param name="Region">Management endpoint the query hit (cloud region identity).</param>
/// <param name="Currency">ISO currency the amounts are in (empty when the response carried none).</param>
/// <param name="From">Window start (inclusive).</param>
/// <param name="To">Window end (inclusive).</param>
/// <param name="AsOf">When the figures were fetched.</param>
public sealed record SpendProvenance(
    string Source,
    string Scope,
    string Region,
    string Currency,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset AsOf);

/// <summary>One cost-management result row; dimension columns the response omitted are null.</summary>
public sealed record SpendRow(
    string? ResourceId,
    string? ResourceGroup,
    string? ServiceFamily,
    string? MeterCategory,
    string? Location,
    decimal Cost,
    string Currency);

public enum SpendDimension { Resource, ResourceGroup, ServiceFamily, MeterCategory, Location }

/// <summary>A dimension bucket: what it costs and its share (0..1) of the dimension total.</summary>
public sealed record SpendBucket(string Key, decimal Cost, double Share);

/// <summary>Rows plus the provenance every figure in them inherits.</summary>
public sealed record ActualSpendResult(IReadOnlyList<SpendRow> Rows, SpendProvenance Provenance);

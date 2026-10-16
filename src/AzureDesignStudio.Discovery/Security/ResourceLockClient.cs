using System.Text.Json;
using AzureDesignStudio.Discovery.Arm;

namespace AzureDesignStudio.Discovery.Security;

/// <summary>
/// Live management locks (v2 plan 80): one subscription-scoped GET returns every lock
/// bound anywhere in that subscription - resource group, resource, or the subscription
/// itself - with nextLink paging on the transport. The engine decides which lock bites
/// which resource; this client only fetches what ARM reports, adding nothing.
/// </summary>
public sealed class ResourceLockClient(ArmTransport transport)
{
    public async Task<IReadOnlyList<LockRow>> ListAtSubscriptionAsync(string subscriptionId, CancellationToken ct = default)
    {
        var items = await transport.GetListAsync(
            $"{transport.Endpoints.ManagementEndpoint}/subscriptions/{Uri.EscapeDataString(subscriptionId)}" +
            "/providers/Microsoft.Authorization/locks?api-version=2020-05-01", "value", ct)
            .ConfigureAwait(false);

        var rows = new List<LockRow>(items.Count);
        foreach (var item in items)
        {
            if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
            var props = item.TryGetProperty("properties", out var p) ? p : default;
            var level = "";
            string? notes = null;
            if (props.ValueKind == JsonValueKind.Object)
            {
                if (props.TryGetProperty("level", out var lvl) && lvl.ValueKind == JsonValueKind.String) level = lvl.GetString()!;
                if (props.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String) notes = n.GetString();
            }
            var name = item.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                ? nm.GetString() ?? "" : "";
            rows.Add(new LockRow(id.GetString()!, name, level, notes));
        }
        return rows;
    }
}

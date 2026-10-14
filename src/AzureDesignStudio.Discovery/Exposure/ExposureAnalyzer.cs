using System.Text.Json;
using AzureDesignStudio.Discovery.Inventory;

namespace AzureDesignStudio.Discovery.Exposure;

public enum ExposureSeverity { Critical, High, Medium, Low }

/// <summary>One internet-exposure finding (v2 plan 9.4).</summary>
/// <param name="Kind">Stable code, e.g. <c>nsg-open-inbound</c>.</param>
/// <param name="Detail">Human-readable specifics (ports, prefixes, endpoint host).</param>
/// <param name="Remediation">Actionable fix, safe to render verbatim.</param>
public sealed record ExposureFinding(
    string ResourceId,
    string ResourceType,
    string Kind,
    ExposureSeverity Severity,
    string Detail,
    string Remediation);

/// <summary>
/// Internet-exposure analysis over a typeless snapshot (v2 plan 9.4): public IPs and
/// what they front, NSG rules opening ports to the whole internet, public PaaS
/// endpoints without a private endpoint, and private endpoints that skipped the
/// Group-Id approval step. Rules evaluate documented ARM property names; an
/// unresolvable link (e.g. cross-subscription id) never crashes a scan.
/// </summary>
public static class ExposureAnalyzer
{
    private static readonly string[] InternetPrefixes = ["*", "0.0.0.0/0", "0.0.0.0", "internet", "any"];
    private static readonly string[] AdminPorts = ["22", "3389"];

    /// <summary>PaaS types with a public internet endpoint unless a private endpoint exists (9.4 v1 set).</summary>
    private static readonly (string Type, string HostProperty)[] PublicPaaS =
    [
        ("microsoft.storage/storageaccounts", "primaryEndpoints.blob"),
        ("microsoft.documentdb/databaseaccounts", "documentEndpoint"),
        ("microsoft.cache/redis", "hostName"),
        ("microsoft.eventhub/namespaces", "serviceEndpoint"),
        ("microsoft.servicebus/namespaces", "serviceBusEndpoint"),
        ("microsoft.dbforpostgresql/servers", "fullyQualifiedDomainName"),
        ("microsoft.web/sites", "hostNames"),
    ];

    public static IReadOnlyList<ExposureFinding> Analyze(IReadOnlyList<InventoryItem> inventory)
    {
        var byId = new Dictionary<string, InventoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in inventory)
            byId[item.Id] = item;

        var findings = new List<ExposureFinding>();
        var protectedByPe = ProtectedResourceIds(inventory);

        foreach (var item in inventory)
        {
            switch (item.Type.ToLowerInvariant())
            {
                case "microsoft.compute/virtualmachines":
                    VmExposure(item, byId, findings);
                    break;
                case "microsoft.network/networksecuritygroups":
                    NsgRules(item, findings);
                    break;
                case "microsoft.network/applicationgateways":
                    FrontendPublicIp(item, findings);
                    break;
                case "microsoft.network/loadbalancers":
                    FrontendPublicIp(item, findings);
                    break;
                case "microsoft.sql/servers":
                    SqlFirewall(item, findings);
                    break;
                case "microsoft.network/privateendpoints":
                    PrivateEndpointApproval(item, findings);
                    break;
            }

            PaasPublic(item, protectedByPe, findings);
        }

        return findings
            .OrderBy(f => f.Severity)
            .ThenBy(f => f.ResourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Kind, StringComparer.Ordinal)
            .ToList();
    }

    // --- virtual machines -------------------------------------------------------

    private static void VmExposure(InventoryItem vm, Dictionary<string, InventoryItem> byId, List<ExposureFinding> findings)
    {
        if (vm.Raw is not { } raw) return;
        if (!raw.TryGetProperty("networkProfile", out var np) || np.ValueKind != JsonValueKind.Object) return;
        if (!np.TryGetProperty("networkInterfaces", out var nics) || nics.ValueKind != JsonValueKind.Array) return;

        foreach (var nicRef in nics.EnumerateArray())
        {
            var nicId = StrProp(nicRef, "id");
            if (nicId.Length == 0 || !byId.TryGetValue(nicId, out var nic)) continue; // cross-scope: skip
            if (nic.Raw is not { } nicRaw) continue;
            if (!nicRaw.TryGetProperty("ipConfigurations", out var ips) || ips.ValueKind != JsonValueKind.Array) continue;

            foreach (var ip in ips.EnumerateArray())
            {
                if (!ip.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object) continue;
                if (!p.TryGetProperty("publicIPAddress", out var pub) || pub.ValueKind != JsonValueKind.Object) continue;
                var pubId = StrProp(pub, "id");
                if (pubId.Length == 0) continue;

                var ipAddr = "";
                var sku = "";
                if (byId.TryGetValue(pubId, out var pubItem) && pubItem.Raw is { } pubRaw)
                {
                    ipAddr = StrProp(pubRaw, "ipAddress");
                    sku = Nested(pubRaw, "sku", "name");
                }

                var shown = ipAddr.Length > 0 ? ipAddr : pubId;
                var admin = AdminPortsOpened(nic, byId);
                if (admin.Count > 0)
                    findings.Add(new ExposureFinding(
                        vm.Id, vm.Type, "vm-public-ip", ExposureSeverity.Critical,
                        $"Public IP {shown} with NSG allowing {string.Join("/", admin)} from the entire internet.",
                        "Remove the direct public IP; reach the VM via Bastion / Jumpbox / Just-in-Time access and close inbound 22/3389."));
                else
                    findings.Add(new ExposureFinding(
                        vm.Id, vm.Type, "vm-public-ip", ExposureSeverity.High,
                        $"Public IP {shown}{(sku.Length > 0 ? $" ({sku} SKU)" : "")} directly attached to the NIC.",
                        "Put the VM behind a Load Balancer / Application Gateway / Bastion and remove the direct public IP."));
            }
        }
    }

    /// <summary>Admin ports (22/3389) open to internet sources through the NIC's NSG; no NSG on the NIC means the platform default applies (no claim).</summary>
    private static IReadOnlyCollection<string> AdminPortsOpened(InventoryItem nic, Dictionary<string, InventoryItem> byId)
    {
        var opened = new List<string>();
        if (nic.Raw is not { } nicRaw) return opened;
        if (!nicRaw.TryGetProperty("networkSecurityGroup", out var nsgRef) || nsgRef.ValueKind != JsonValueKind.Object)
            return opened; // no NSG attached: defaults vary by placement - no claim in v1
        var nsgId = StrProp(nsgRef, "id");
        if (nsgId.Length == 0 || !byId.TryGetValue(nsgId, out var nsg)) return opened; // unresolvable: no claim

        foreach (var (ports, source, action) in InboundRules(nsg))
            if (action && Internet(source) && ports.Intersect(AdminPorts, StringComparer.Ordinal).Any())
                opened.AddRange(ports.Intersect(AdminPorts, StringComparer.Ordinal));

        return opened.Distinct(StringComparer.Ordinal).ToList();
    }

    // --- network security groups --------------------------------------------------

    private static void NsgRules(InventoryItem nsg, List<ExposureFinding> findings)
    {
        foreach (var (ports, source, action) in InboundRules(nsg))
        {
            if (!action || !Internet(source)) continue;
            if (ports.Contains("*") || ports.Contains("0-65535") || ports.Contains("any"))
            {
                findings.Add(new ExposureFinding(
                    nsg.Id, nsg.Type, "nsg-open-all-inbound", ExposureSeverity.Critical,
                    $"Inbound rule allows the entire internet (source {source}) on all ports.",
                    "Restrict the source to specific address ranges or use Azure Bastion / Just-in-Time VM access."));
            }
            else if (ports.Intersect(AdminPorts, StringComparer.Ordinal).Any())
            {
                findings.Add(new ExposureFinding(
                    nsg.Id, nsg.Type, "nsg-open-admin-ports", ExposureSeverity.High,
                    $"Inbound rule allows the entire internet (source {source}) on admin port(s) {string.Join(", ", ports.Intersect(AdminPorts, StringComparer.Ordinal))}.",
                    "Restrict the source to specific address ranges or use Azure Bastion / Just-in-Time VM access."));
            }
        }
    }

    private static IEnumerable<(string[] Ports, string Source, bool Allow)> InboundRules(InventoryItem nsg)
    {
        if (nsg.Raw is not { } raw) yield break;
        if (!raw.TryGetProperty("securityRules", out var rules) || rules.ValueKind != JsonValueKind.Array) yield break;

        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object) continue;
            // Built-in rules start with "default" (DenyAllInBound etc.) - only user rules are judged.
            var name = StrProp(rule, "name");
            if (name.StartsWith("default", StringComparison.OrdinalIgnoreCase)) continue;

            var p = rule.TryGetProperty("properties", out var pr) && pr.ValueKind == JsonValueKind.Object ? pr : rule;
            var direction = StrProp(p, "direction");
            if (direction.Length > 0 && !direction.Equals("Inbound", StringComparison.OrdinalIgnoreCase)) continue;

            var allow = !StrProp(p, "access").Equals("Deny", StringComparison.OrdinalIgnoreCase);
            var source = StrProp(p, "sourceAddressPrefix");
            if (source.Length == 0 && p.TryGetProperty("sourceAddressPrefixes", out var srcs) && srcs.ValueKind == JsonValueKind.Array)
                source = string.Join(",", srcs.EnumerateArray().Select(e => e.GetString() ?? ""));

            yield return (PortsOf(p), source, allow);
        }
    }

    private static string[] PortsOf(JsonElement props)
    {
        if (props.TryGetProperty("destinationPortRanges", out var ranges) && ranges.ValueKind == JsonValueKind.Array)
            return ranges.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray();
        var single = StrProp(props, "destinationPortRange");
        return single.Length > 0 ? [single] : ["*"];
    }

    private static bool Internet(string source) =>
        source.Length > 0 && InternetPrefixes.Contains(source.Split(',')[0].Trim(), StringComparer.OrdinalIgnoreCase);

    // --- front-door resources -------------------------------------------------------

    private static void FrontendPublicIp(InventoryItem gw, List<ExposureFinding> findings)
    {
        if (gw.Raw is not { } raw) return;
        var p = raw.TryGetProperty("properties", out var pr) && pr.ValueKind == JsonValueKind.Object ? pr : raw;
        if (!p.TryGetProperty("frontendIPConfigurations", out var fips) || fips.ValueKind != JsonValueKind.Array) return;

        foreach (var fip in fips.EnumerateArray())
        {
            var fp = fip.TryGetProperty("properties", out var fpp) && fpp.ValueKind == JsonValueKind.Object ? fpp : fip;
            if (!fp.TryGetProperty("publicIPAddress", out var pub) || pub.ValueKind != JsonValueKind.Object) continue;
            var pubId = StrProp(pub, "id");
            var ip = StrProp(fp, "publicIPAddress");
            findings.Add(new ExposureFinding(
                gw.Id, gw.Type, "lb-frontend-public", ExposureSeverity.Medium,
                $"Public frontend IP {(ip.Length > 0 ? ip : pubId)}; exposure depends on its rules/WAF.",
                "Confirm the backend pools, load-balancing and WAF policies are intended for internet traffic."));
        }
    }

    // --- public PaaS endpoints --------------------------------------------------------

    /// <summary>Resource ids that at least one private endpoint connects to.</summary>
    private static HashSet<string> ProtectedResourceIds(IReadOnlyList<InventoryItem> inventory)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pe in inventory.Where(i => i.Type.Equals("microsoft.network/privateendpoints", StringComparison.OrdinalIgnoreCase)))
        {
            if (pe.Raw is not { } raw) continue;
            var p = raw.TryGetProperty("properties", out var pr) && pr.ValueKind == JsonValueKind.Object ? pr : raw;
            if (!p.TryGetProperty("privateLinkServiceConnections", out var conns) || conns.ValueKind != JsonValueKind.Array) continue;
            foreach (var c in conns.EnumerateArray())
            {
                var cp = c.TryGetProperty("properties", out var cpp) && cpp.ValueKind == JsonValueKind.Object ? cpp : c;
                var target = StrProp(cp, "privateLinkServiceId");
                if (target.Length > 0) ids.Add(target);
            }
        }

        return ids;
    }

    private static void PaasPublic(InventoryItem item, HashSet<string> protectedIds, List<ExposureFinding> findings)
    {
        var type = item.Type.ToLowerInvariant();
        var hostProperty = "";
        foreach (var (t, hp) in PublicPaaS)
            if (t == type) { hostProperty = hp; break; }
        if (hostProperty.Length == 0) return;
        if (item.Raw is not { } raw) return;
        if (protectedIds.Contains(item.Id)) return;

        var host = ResolveHost(raw, hostProperty);
        if (host.Length == 0) return; // no public endpoint value present

        findings.Add(new ExposureFinding(
            item.Id, item.Type, "paas-public-endpoint", ExposureSeverity.Medium,
            $"Public endpoint {host} reachable from the internet; no private endpoint found.",
            "Disable public network access (or use signed/ACL access) and add a Private Endpoint."));

        if (type == "microsoft.web/sites" && BoolProp(raw, "httpsOnly") == false)
            findings.Add(new ExposureFinding(
                item.Id, item.Type, "appservice-http-allowed", ExposureSeverity.Low,
                $"HTTPS-only is disabled on {host}.", "Enable httpsOnly."));
    }

    /// <summary>Dotted host path (<c>primaryEndpoints.blob</c>) or plain property, tolerated at Raw root or under properties.</summary>
    private static string ResolveHost(JsonElement raw, string hostProperty)
    {
        var parts = hostProperty.Split('.');
        foreach (var root in EnumScopes(raw))
        {
            var cur = root;
            var ok = true;
            foreach (var part in parts)
            {
                if (part == "properties") continue; // Raw IS the properties object
                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(part, out var next)) { ok = false; break; }
                cur = next;
            }

            if (!ok) continue;
            if (cur.ValueKind == JsonValueKind.String) return cur.GetString() ?? "";
            if (cur.ValueKind == JsonValueKind.Array && cur.GetArrayLength() > 0)
                return cur[0].GetString() ?? "";
        }

        return "";
    }

    private static IEnumerable<JsonElement> EnumScopes(JsonElement raw)
    {
        yield return raw;
        if (raw.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
            yield return p;
    }

    // --- SQL firewall -----------------------------------------------------------------

    private static void SqlFirewall(InventoryItem server, List<ExposureFinding> findings)
    {
        if (server.Raw is not { } raw) return;
        if (!raw.TryGetProperty("firewallRules", out var rules) || rules.ValueKind != JsonValueKind.Array) return;

        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object) continue;
            var rp = rule.TryGetProperty("properties", out var prp) && prp.ValueKind == JsonValueKind.Object ? prp : rule;
            if (StrProp(rp, "startIpAddress") == "0.0.0.0" && StrProp(rp, "endIpAddress") == "0.0.0.0")
                findings.Add(new ExposureFinding(
                    server.Id, server.Type, "sql-firewall-all", ExposureSeverity.Critical,
                    "Firewall rule 0.0.0.0-0.0.0.0 allows the entire internet.",
                    "Delete the 0.0.0.0 rule; use a Private Endpoint or specific client ranges."));
        }
    }

    // --- private endpoint approval ------------------------------------------------------

    private static void PrivateEndpointApproval(InventoryItem pe, List<ExposureFinding> findings)
    {
        if (pe.Raw is not { } raw) return;
        var p = raw.TryGetProperty("properties", out var pr) && pr.ValueKind == JsonValueKind.Object ? pr : raw;
        if (!p.TryGetProperty("privateLinkServiceConnections", out var conns) || conns.ValueKind != JsonValueKind.Array) return;

        foreach (var c in conns.EnumerateArray())
        {
            var cp = c.TryGetProperty("properties", out var cpp) && cpp.ValueKind == JsonValueKind.Object ? cpp : c;
            var state = StrProp(cp, "privateLinkServiceConnectionStateStatus");
            var approval = StrProp(cp, "privateLinkServiceConnectionStateActionsRequired");
            if (state.Length > 0 && !state.Equals("Approved", StringComparison.OrdinalIgnoreCase))
                findings.Add(new ExposureFinding(
                    pe.Id, pe.Type, "pe-pending-approval", ExposureSeverity.Low,
                    $"Private link connection state {state} (actions required: {(approval.Length > 0 ? approval : "none")}).",
                    "Approve the private endpoint connection on the PaaS resource side."));
        }
    }

    // --- helpers -------------------------------------------------------------------------

    private static string StrProp(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return "";
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    private static string Nested(JsonElement el, string outer, string inner) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(outer, out var o) && o.ValueKind == JsonValueKind.Object
            ? StrProp(o, inner) : "";

    private static bool? BoolProp(JsonElement raw, string property)
    {
        foreach (var scope in EnumScopes(raw))
        {
            if (scope.ValueKind != JsonValueKind.Object) continue;
            if (scope.TryGetProperty(property, out var v))
                return v.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => !string.Equals(v.GetString(), "false", StringComparison.OrdinalIgnoreCase),
                    _ => null,
                };
        }

        return null;
    }
}

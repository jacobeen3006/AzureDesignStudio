using System.Text;
using System.Text.Json;
using AzureDesignStudio.Discovery.Arm;

namespace AzureDesignStudio.Discovery.Iac.WhatIf;

/// <summary>
/// ARM what-if preflight [50]: POST {rg}/providers/Microsoft.Resources/deployments/{name}/whatIf,
/// follows the LRO (202 + Location) through <see cref="ArmTransport"/> (shared 429 budget).
/// The ARM what-if result already carries full resource payloads in before/after; there is no
/// separate FullResourcePayloads switch on the REST path - predictions stay predictions (see caveats).
/// </summary>
public sealed class WhatIfClient(ArmTransport transport)
{
    /// <summary>The deployment name must match the real deploy so what-if evaluates the same identity.</summary>
    public const string DeploymentName = "ads-deployment";

    private const string ApiVersion = "2025-04-01";

    public async Task<WhatIfSummary> WhatIfAsync(string subscriptionId, string resourceGroupName, string location,
        string armTemplate, string parameters, CancellationToken ct = default)
    {
        var url = $"{transport.Endpoints.ManagementEndpoint}/subscriptions/{subscriptionId}/resourcegroups/{resourceGroupName}"
            + $"/providers/Microsoft.Resources/deployments/{DeploymentName}/whatIf?api-version={ApiVersion}";

        var body = BuildRequestBody(location, armTemplate, parameters);
        var responseJson = await transport.PostAsyncPolled(url, body, maxPolls: 60, ct).ConfigureAwait(false);
        return WhatIfParser.Parse(responseJson);
    }

    internal static string BuildRequestBody(string location, string armTemplate, string parameters)
    {
        using var template = ParseObject(armTemplate, "template");
        using var parametersDoc = string.IsNullOrWhiteSpace(parameters) ? null : ParseObject(parameters, "parameters");

        var sb = new StringBuilder();
        sb.Append("{\"location\":").Append(JsonSerializer.Serialize(location));
        sb.Append(",\"properties\":{\"mode\":\"Incremental\"");
        sb.Append(",\"template\":").Append(template.RootElement.GetRawText());
        sb.Append(",\"parameters\":").Append(parametersDoc is null ? "{}" : parametersDoc.RootElement.GetRawText());
        sb.Append("}}");
        return sb.ToString();
    }

    private static JsonDocument ParseObject(string json, string what)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new WhatIfException("MalformedTemplate", $"{what} is not valid JSON: {ex.Message}");
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new WhatIfException("MalformedTemplate", $"{what} must be a JSON object.");
        }

        return doc;
    }
}

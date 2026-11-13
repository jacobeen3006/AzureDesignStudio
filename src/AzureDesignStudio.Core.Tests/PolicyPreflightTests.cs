using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Discovery.Preflight;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// Wave B2: the Azure Policy preflight reads the what-if after payloads and asks
/// checkPolicyRestrictions about each resource. It is advisory: secrets never reach the
/// request body, verdicts come only from policyEvaluations, and every failure (no
/// permission, throttling, a malformed answer, the time limit) is a note with the
/// findings gathered so far - never a silent "no findings". All offline, scripted ARM.
/// </summary>
public class PolicyPreflightTests
{
    private const string Sub = "00000000-0000-0000-0000-0000000000a1";
    private const string Rg = $"/subscriptions/{Sub}/resourceGroups/rg-app";
    private const string RgPolicyPath = $"/subscriptions/{Sub}/resourceGroups/rg-app/providers/Microsoft.PolicyInsights/checkPolicyRestrictions";
    private const string ForbiddenNote = "No permission to check Azure Policy (needs Microsoft.PolicyInsights/checkPolicyRestrictions/read).";

    private const string EmptyTemplate = """
        {"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","parameters":{},"resources":[]}
        """;

    private static readonly IReadOnlySet<string> NoSecrets = new HashSet<string>();

    // ---------- fakes ----------

    private sealed record Sent(string Method, string Path, string Query, string Body)
    {
        public string? ContentName =>
            JsonNode.Parse(Body)?["resourceDetails"]?["resourceContent"]?["name"]?.GetValue<string>();
    }

    private sealed class PolicyHandler(Func<Sent, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Sent> _sent = new();

        public IReadOnlyList<Sent> Sent => _sent.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var sent = new Sent(request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query, body);
            _sent.Enqueue(sent);
            return await respond(sent, cancellationToken);
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new AccessToken("fake-token", DateTimeOffset.MaxValue));
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
            => new("fake-token", DateTimeOffset.MaxValue);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static PolicyHandler Answering(Func<Sent, HttpResponseMessage> respond) =>
        new((sent, _) => Task.FromResult(respond(sent)));

    private static PolicyPreflight Engine(PolicyHandler handler, TimeSpan? httpTimeout = null)
    {
        var http = new HttpClient(handler);
        if (httpTimeout is { } timeout)
            http.Timeout = timeout;
        return new(new ArmTransport(http, new FakeCredential(), ArmEndpointSet.AzureCloud, (_, _) => Task.CompletedTask));
    }

    private static string ArmError(string code, string message) =>
        JsonSerializer.Serialize(new { error = new { code, message } });

    private static HttpResponseMessage Forbidden() =>
        Json(ArmError("AuthorizationFailed",
            "The client does not have authorization to perform action 'Microsoft.PolicyInsights/checkPolicyRestrictions/read'."), HttpStatusCode.Forbidden);

    // ---------- fixtures (shapes from "Facts verified on Microsoft Learn") ----------

    private static string StorageId(int i) => $"{Rg}/providers/Microsoft.Storage/storageAccounts/sa{i:D2}";

    private static WhatIfChange Change(string resourceId, string? type, string? apiVersion = "2023-05-01",
        string properties = "{}", WhatIfChangeKind kind = WhatIfChangeKind.Create)
    {
        var after = new JsonObject
        {
            ["id"] = resourceId,
            ["name"] = resourceId[(resourceId.LastIndexOf('/') + 1)..],
            ["location"] = "westeurope",
            ["properties"] = JsonNode.Parse(properties),
        };
        if (type is not null) after["type"] = type;
        if (apiVersion is not null) after["apiVersion"] = apiVersion;
        return new WhatIfChange(kind, resourceId, kind == WhatIfChangeKind.Create ? null : "{}", after.ToJsonString(), 0);
    }

    private static WhatIfChange Storage(int i) => Change(StorageId(i), "Microsoft.Storage/storageAccounts");

    private static string Evaluation(string effect, string assignmentName, string evaluationResult = "NonCompliant",
        string reason = "Storage accounts must not allow public blob access.") => $$"""
        {
          "policyInfo": {
            "policyDefinitionId": "/providers/Microsoft.Authorization/policyDefinitions/4fa4b6c0-31ca-4c0d-b10d-24b96f62a751",
            "policySetDefinitionId": "/providers/Microsoft.Authorization/policySetDefinitions/baseline",
            "policyDefinitionReferenceId": "storageDisallowPublicAccess",
            "policyAssignmentId": "/subscriptions/{{Sub}}/providers/Microsoft.Authorization/policyAssignments/{{assignmentName}}"
          },
          "evaluationResult": "{{evaluationResult}}",
          "evaluationDetails": {
            "evaluatedExpressions": [
              { "result": "True", "expressionKind": "Field", "expression": "Microsoft.Storage/storageAccounts/allowBlobPublicAccess",
                "path": "properties.allowBlobPublicAccess", "expressionValue": true, "targetValue": false, "operator": "Equals" }
            ],
            "reason": "{{reason}}"
          },
          "effectDetails": { "policyEffect": "{{effect}}" }
        }
        """;

    private static string Response(params string[] evaluations) =>
        $$$"""{"fieldRestrictions":[],"contentEvaluationResult":{"policyEvaluations":[{{{string.Join(",", evaluations)}}}]}}""";

    private static readonly string DenyResponse = Response(Evaluation("Deny", "baseline"));

    private static JsonObject Content(string requestJson) =>
        JsonNode.Parse(requestJson)!["resourceDetails"]!["resourceContent"]!.AsObject();

    /// <summary>Every property name and string value in the document, so a secret is found
    /// whatever escaping the serializer chose.</summary>
    private static List<string> AllText(JsonNode? node)
    {
        var text = new List<string>();
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var (key, value) in o) { text.Add(key); Walk(value); }
                    break;
                case JsonArray a:
                    foreach (var item in a) Walk(item);
                    break;
                case JsonValue v when v.TryGetValue<string>(out var s):
                    text.Add(s);
                    break;
            }
        }
        Walk(node);
        return text;
    }

    private static void AssertAbsent(string requestJson, params string[] secrets)
    {
        var text = AllText(JsonNode.Parse(requestJson));
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, requestJson, StringComparison.Ordinal);
            Assert.DoesNotContain(text, t => t.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string Build(WhatIfChange change, IReadOnlySet<string>? secureValues = null, string? template = EmptyTemplate)
    {
        var body = PolicyPreflight.BuildRequest(change.ResourceId!, change.AfterJson!, template, secureValues ?? NoSecrets, out var skip);
        Assert.True(body is not null, $"request was skipped: {skip}");
        Assert.Null(skip);
        return body!;
    }

    // ---------- scrub: secrets never go to Azure Policy ----------

    [Fact]
    public void Scrub_VmAdminPassword_Removed_DisablePasswordAuthenticationKept()
    {
        var vm = Change($"{Rg}/providers/Microsoft.Compute/virtualMachines/vm1", "Microsoft.Compute/virtualMachines", "2024-07-01", """
            {
              "hardwareProfile": { "vmSize": "Standard_D2s_v5" },
              "osProfile": {
                "computerName": "vm1",
                "adminUsername": "azureuser",
                "adminPassword": "P@ssw0rd-Very-Secret",
                "linuxConfiguration": { "disablePasswordAuthentication": true, "provisionVMAgent": true }
              }
            }
            """);

        var body = Build(vm);

        AssertAbsent(body, "P@ssw0rd-Very-Secret", "adminPassword");
        var osProfile = Content(body)["properties"]!["osProfile"]!;
        Assert.Equal("azureuser", osProfile["adminUsername"]!.GetValue<string>());
        Assert.True(osProfile["linuxConfiguration"]!["disablePasswordAuthentication"]!.GetValue<bool>());
        Assert.Equal("Standard_D2s_v5", Content(body)["properties"]!["hardwareProfile"]!["vmSize"]!.GetValue<string>());
    }

    [Fact]
    public void Scrub_FunctionAppAppSettingsValues_Removed_NamesKept()
    {
        const string storageConnection = "DefaultEndpointsProtocol=https;AccountName=stfunc01;AccountKey=c2VjcmV0LWtleS12YWx1ZQ==;EndpointSuffix=core.windows.net";
        var func = Change($"{Rg}/providers/Microsoft.Web/sites/func-app", "Microsoft.Web/sites", "2023-12-01", $$"""
            {
              "httpsOnly": true,
              "siteConfig": {
                "minTlsVersion": "1.2",
                "appSettings": [
                  { "name": "AzureWebJobsStorage", "value": "{{storageConnection}}" },
                  { "name": "FUNCTIONS_EXTENSION_VERSION", "value": "~4" }
                ],
                "connectionStrings": [ { "name": "Db", "connectionString": "Server=tcp:sql1;Password=db-pass-1", "type": "SQLAzure" } ]
              }
            }
            """);

        var body = Build(func);

        AssertAbsent(body, "AccountKey=", "c2VjcmV0LWtleS12YWx1ZQ==", "db-pass-1", "connectionStrings");
        var siteConfig = Content(body)["properties"]!["siteConfig"]!;
        var settings = siteConfig["appSettings"]!.AsArray();
        Assert.Equal(["AzureWebJobsStorage", "FUNCTIONS_EXTENSION_VERSION"], settings.Select(s => s!["name"]!.GetValue<string>()));
        Assert.All(settings, s => Assert.False(s!.AsObject().ContainsKey("value")));
        Assert.Equal("1.2", siteConfig["minTlsVersion"]!.GetValue<string>());
        Assert.True(Content(body)["properties"]!["httpsOnly"]!.GetValue<bool>());
    }

    [Fact]
    public void Scrub_ProtectedSettingsAndCustomData_Removed()
    {
        var extension = Change($"{Rg}/providers/Microsoft.Compute/virtualMachines/vm1/extensions/install", "Microsoft.Compute/virtualMachines/extensions", "2024-07-01", """
            {
              "publisher": "Microsoft.Azure.Extensions",
              "type": "CustomScript",
              "settings": {
                "commandToExecute": "bash install.sh",
                "fileUris": [
                  "https://stscripts.blob.core.windows.net/scripts/install.sh?sv=2022-11-02&sr=b&sig=AbCdEf0123%3D",
                  "https://raw.githubusercontent.com/example/install.sh"
                ]
              },
              "protectedSettings": { "storageAccountName": "stscripts", "storageAccountKey": "protected-key-value" }
            }
            """);
        var vm = Change($"{Rg}/providers/Microsoft.Compute/virtualMachines/vm2", "Microsoft.Compute/virtualMachines", "2024-07-01", """
            { "osProfile": { "computerName": "vm2", "adminUsername": "azureuser", "customData": "I2Nsb3VkLWNvbmZpZyBzZWNyZXQ=" } }
            """);

        var extensionBody = Build(extension);
        var vmBody = Build(vm);

        AssertAbsent(extensionBody, "protectedSettings", "protected-key-value", "sig=AbCdEf0123");
        var settings = Content(extensionBody)["properties"]!["settings"]!;
        Assert.Equal("bash install.sh", settings["commandToExecute"]!.GetValue<string>());
        Assert.Equal(["https://raw.githubusercontent.com/example/install.sh"], settings["fileUris"]!.AsArray().Select(u => u!.GetValue<string>()));

        AssertAbsent(vmBody, "customData", "I2Nsb3VkLWNvbmZpZyBzZWNyZXQ=");
        Assert.Equal("vm2", Content(vmBody)["properties"]!["osProfile"]!["computerName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Scrub_SecureParameterValue_RemovedWherever_ItAppears()
    {
        const string template = """
            {
              "parameters": {
                "sqlAdminPassword": { "type": "secureString" },
                "apiKey": { "type": "securestring", "defaultValue": "default-api-key-123" },
                "plain": { "type": "string" }
              },
              "resources": []
            }
            """;
        // Parameter names are case-insensitive in ARM.
        const string parameters = """{ "SQLADMINPASSWORD": { "value": "Sup3r-S3cret-Value" }, "plain": { "value": "visible-value" } }""";
        var app = Change($"{Rg}/providers/Microsoft.Web/sites/app1", "Microsoft.Web/sites", "2023-12-01", """
            {
              "description": "prefix-Sup3r-S3cret-Value-suffix",
              "visible": "visible-value",
              "list": [ "keep-me", "Sup3r-S3cret-Value" ],
              "header": "x-api-key default-api-key-123"
            }
            """);
        var withTag = JsonNode.Parse(app.AfterJson!)!.AsObject();
        withTag["tags"] = new JsonObject { ["note"] = "Sup3r-S3cret-Value", ["owner"] = "team-a" };
        app = app with { AfterJson = withTag.ToJsonString() };

        var secure = PolicyPreflight.SecureValues(template, parameters);
        Assert.Contains("Sup3r-S3cret-Value", secure);
        Assert.Contains("default-api-key-123", secure);
        Assert.DoesNotContain("visible-value", secure);

        var body = Build(app, secure, template);
        AssertAbsent(body, "Sup3r-S3cret-Value", "default-api-key-123");
        var content = Content(body);
        Assert.Equal("visible-value", content["properties"]!["visible"]!.GetValue<string>());
        Assert.Equal(["keep-me"], content["properties"]!["list"]!.AsArray().Select(i => i!.GetValue<string>()));
        Assert.Equal("team-a", content["tags"]!["owner"]!.GetValue<string>());

        // End to end: CheckAsync reads the template and parameters itself.
        var handler = Answering(_ => Json(Response()));
        var result = await Engine(handler).CheckAsync([app], template, parameters, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(1, result.Checked);
        AssertAbsent(Assert.Single(handler.Sent).Body, "Sup3r-S3cret-Value", "default-api-key-123");
    }

    [Fact]
    public void Scrub_SecureObjectLeaves_Removed()
    {
        const string template = """
            {
              "parameters": {
                "settings": { "type": "secureObject" },
                "fallback": { "type": "secureobject", "defaultValue": { "token": "default-object-leaf" } }
              },
              "resources": []
            }
            """;
        const string parameters = """
            { "settings": { "value": { "user": "obj-user-1", "nested": { "key": "obj-key-2" }, "list": [ "obj-item-3" ], "count": 7 } } }
            """;
        var app = Change($"{Rg}/providers/Microsoft.Web/sites/app2", "Microsoft.Web/sites", "2023-12-01", """
            { "first": "obj-user-1", "second": { "deep": "has obj-key-2 inside" }, "third": [ "obj-item-3", "plain" ], "fourth": "default-object-leaf", "count": 7 }
            """);

        var secure = PolicyPreflight.SecureValues(template, parameters);
        var body = Build(app, secure, template);

        AssertAbsent(body, "obj-user-1", "obj-key-2", "obj-item-3", "default-object-leaf");
        var properties = Content(body)["properties"]!;
        Assert.Equal(["plain"], properties["third"]!.AsArray().Select(i => i!.GetValue<string>()));
        Assert.Equal(7, properties["count"]!.GetValue<int>());
    }

    [Fact]
    public void Scrub_NonSecretBooleansAndNumbers_Kept()
    {
        var aks = Change($"{Rg}/providers/Microsoft.ContainerService/managedClusters/aks1", "Microsoft.ContainerService/managedClusters", "2024-09-01", """
            {
              "addonProfiles": {
                "azureKeyvaultSecretsProvider": { "enabled": true, "config": { "enableSecretRotation": "true", "rotationPollInterval": "2m" } }
              },
              "agentPoolProfiles": [ { "name": "system", "count": 3, "vmSize": "Standard_D4s_v5", "maxPods": 110 } ],
              "windowsProfile": { "adminUsername": "azureuser", "adminPassword": "Win-P@ss-123", "enableCSIProxy": true },
              "limits": {
                "passwordMinLength": 12, "secretCount": 0, "tokenLifetimeMinutes": 60, "sasEnabled": false,
                "accessKeyRotationDays": 90, "accountKeyCount": 2, "connectionStringPooling": true
              }
            }
            """);

        var body = Build(aks);

        AssertAbsent(body, "Win-P@ss-123");
        var properties = Content(body)["properties"]!;
        var provider = properties["addonProfiles"]!["azureKeyvaultSecretsProvider"]!;
        Assert.True(provider["enabled"]!.GetValue<bool>());
        // Name-based removal takes string leaves only, at any depth under the secret name (the
        // provider's own name holds "Secret"): both config strings go, the boolean stays.
        Assert.False(provider["config"]!.AsObject().ContainsKey("enableSecretRotation"));
        Assert.False(provider["config"]!.AsObject().ContainsKey("rotationPollInterval"));
        Assert.Equal(3, properties["agentPoolProfiles"]![0]!["count"]!.GetValue<int>());
        Assert.True(properties["windowsProfile"]!["enableCSIProxy"]!.GetValue<bool>());

        var limits = properties["limits"]!.AsObject();
        Assert.Equal(12, limits["passwordMinLength"]!.GetValue<int>());
        Assert.Equal(0, limits["secretCount"]!.GetValue<int>());
        Assert.Equal(60, limits["tokenLifetimeMinutes"]!.GetValue<int>());
        Assert.False(limits["sasEnabled"]!.GetValue<bool>());
        Assert.Equal(90, limits["accessKeyRotationDays"]!.GetValue<int>());
        Assert.Equal(2, limits["accountKeyCount"]!.GetValue<int>());
        Assert.True(limits["connectionStringPooling"]!.GetValue<bool>());
    }

    [Fact]
    public void Scrub_DataFactorySecureStringValues_Removed_WhereverTheySit()
    {
        // A Data Factory linked service holds its secrets as { "type": "SecureString", "value": ... }:
        // under a secret name (password), under a name no key rule knows (servicePrincipalKey), as a
        // SecureString parameter's default, and as a SecureObject inside an array.
        var linked = Change($"{Rg}/providers/Microsoft.DataFactory/factories/adf1/linkedservices/AzureSqlDb",
            "Microsoft.DataFactory/factories/linkedservices", "2018-06-01", """
            {
              "type": "AzureSqlDatabase",
              "connectVia": { "referenceName": "AutoResolveIntegrationRuntime", "type": "IntegrationRuntimeReference" },
              "parameters": { "spnDefault": { "type": "SecureString", "defaultValue": "adf-param-default-3" } },
              "annotations": [ { "type": "SecureObject", "value": { "note": "adf-object-leaf-4" } }, "team-data" ],
              "typeProperties": {
                "server": "sql1.database.windows.net",
                "database": "db1",
                "authenticationType": "ServicePrincipal",
                "userName": "adfuser",
                "password": { "type": "SecureString", "value": "Adf-P@ss-1" },
                "servicePrincipalId": "11111111-2222-3333-4444-555555555555",
                "servicePrincipalKey": { "type": "securestring", "value": "adf-spn-key-2" },
                "tenant": "contoso.onmicrosoft.com"
              }
            }
            """);

        var body = Build(linked);

        AssertAbsent(body, "Adf-P@ss-1", "adf-spn-key-2", "adf-param-default-3", "adf-object-leaf-4");
        var properties = Content(body)["properties"]!;
        var typeProperties = properties["typeProperties"]!;
        Assert.Equal("AzureSqlDatabase", properties["type"]!.GetValue<string>());
        Assert.Equal("sql1.database.windows.net", typeProperties["server"]!.GetValue<string>());
        Assert.Equal("adfuser", typeProperties["userName"]!.GetValue<string>());
        Assert.Equal("11111111-2222-3333-4444-555555555555", typeProperties["servicePrincipalId"]!.GetValue<string>());
        Assert.Equal("securestring", typeProperties["servicePrincipalKey"]!["type"]!.GetValue<string>());
        Assert.Equal("AutoResolveIntegrationRuntime", properties["connectVia"]!["referenceName"]!.GetValue<string>());
        Assert.Equal("IntegrationRuntimeReference", properties["connectVia"]!["type"]!.GetValue<string>());
        Assert.Equal("team-data", properties["annotations"]![1]!.GetValue<string>());
    }

    [Theory]
    // Container Instances secret volume: file name -> base64 content, under "secret".
    [InlineData("""{ "kept": "plain-value", "volumes": [ { "name": "certs", "secret": { "tls.key": "nested-secret-1", "count": 2, "enabled": true } } ] }""")]
    [InlineData("""{ "kept": "plain-value", "secrets": { "db": "nested-secret-1", "count": 2, "enabled": true } }""")]
    [InlineData("""{ "kept": "plain-value", "accountKey": { "primary": { "current": [ "nested-secret-1" ], "count": 2, "enabled": true } } }""")]
    public void Scrub_StringsAtAnyDepthUnderASecretName_Removed(string properties)
    {
        var group = Change($"{Rg}/providers/Microsoft.ContainerInstance/containerGroups/aci1", "Microsoft.ContainerInstance/containerGroups",
            "2023-05-01", properties);

        var body = Build(group);

        AssertAbsent(body, "nested-secret-1");
        Assert.Equal("plain-value", Content(body)["properties"]!["kept"]!.GetValue<string>());
        Assert.Contains("\"count\":2", body, StringComparison.Ordinal);
        Assert.Contains("\"enabled\":true", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AccountKey=")]
    [InlineData("SharedAccessKey=")]
    [InlineData("SharedAccessSignature=")]
    [InlineData("sig=")]
    [InlineData("-----BEGIN")]
    public void Scrub_SecretPatterns_RemovedFromAnyStringLeaf(string pattern)
    {
        var app = Change($"{Rg}/providers/Microsoft.Web/sites/app3", "Microsoft.Web/sites", "2023-12-01", $$"""
            { "harmless": "{{pattern}}xyz-leaked", "kept": "plain-value" }
            """);

        var body = Build(app);

        AssertAbsent(body, "xyz-leaked");
        Assert.Equal("plain-value", Content(body)["properties"]!["kept"]!.GetValue<string>());
    }

    // ---------- request shape ----------

    [Theory]
    [InlineData("/providers/Microsoft.KeyVault/vaults/kv1/secrets/db-password", "Microsoft.KeyVault/vaults/secrets")]
    [InlineData("/providers/Microsoft.KeyVault/vaults/kv1/keys/cmk", "microsoft.keyvault/vaults/keys")]
    [InlineData("/providers/Microsoft.Web/sites/app1/config/appsettings", "Microsoft.Web/sites/config")]
    [InlineData("/providers/Microsoft.Web/sites/app1/slots/staging/config/appsettings", "Microsoft.Web/sites/slots/config")]
    [InlineData("/providers/Microsoft.ApiManagement/service/apim1/namedValues/backend-key", "Microsoft.ApiManagement/service/namedValues")]
    [InlineData("/providers/Microsoft.KeyVault/vaults/kv1/secrets/no-type-in-payload", null)]
    public void BuildRequest_SecretTypes_AreSkipped(string relativeId, string? type)
    {
        var change = Change(Rg + relativeId, type, "2023-07-01", """{ "value": "the-secret" }""");

        var body = PolicyPreflight.BuildRequest(change.ResourceId!, change.AfterJson!, EmptyTemplate, NoSecrets, out var skip);

        Assert.Null(body);
        Assert.False(string.IsNullOrWhiteSpace(skip));
    }

    [Fact]
    public async Task Check_SecretTypes_CountAsNotChecked_WithANote()
    {
        var secretId = $"{Rg}/providers/Microsoft.KeyVault/vaults/kv1/secrets/db-password";
        var handler = Answering(_ => Json(Response()));

        var result = await Engine(handler).CheckAsync(
            [Change(secretId, "Microsoft.KeyVault/vaults/secrets", "2023-07-01", """{ "value": "the-secret" }"""), Storage(1)],
            EmptyTemplate, null, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal("sa01", Assert.Single(handler.Sent).ContentName);
        Assert.Equal(1, result.Checked);
        Assert.Equal(1, result.NotChecked);
        Assert.Contains(secretId, Assert.Single(result.Notes));
    }

    [Fact]
    public void BuildRequest_KeepsOnlyResourceMembers()
    {
        const string after = """
            {
              "id": "/subscriptions/00000000-0000-0000-0000-0000000000a1/resourceGroups/rg-app/providers/Microsoft.Web/sites/app1",
              "name": "app1", "type": "Microsoft.Web/sites", "apiVersion": "2023-12-01", "location": "westeurope",
              "tags": { "env": "prod" }, "sku": { "name": "P1v3" }, "kind": "app", "identity": { "type": "SystemAssigned" },
              "zones": [ "1" ], "plan": { "name": "plan1" }, "extendedLocation": { "name": "edge1", "type": "EdgeZone" },
              "properties": { "httpsOnly": true },
              "etag": "W/\"1\"", "managedBy": "/subscriptions/x", "systemData": { "createdBy": "someone@example.com" },
              "dependsOn": [ "x" ], "resourceGroup": "rg-app", "condition": true, "copy": { "name": "c", "count": 2 }
            }
            """;

        var body = PolicyPreflight.BuildRequest($"{Rg}/providers/Microsoft.Web/sites/app1", after, EmptyTemplate, NoSecrets, out var skip);

        Assert.Null(skip);
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal(["includeAuditEffect", "resourceDetails"], doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.True(doc.RootElement.GetProperty("includeAuditEffect").GetBoolean());
        var details = doc.RootElement.GetProperty("resourceDetails");
        Assert.Equal(["apiVersion", "resourceContent"], details.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("2023-12-01", details.GetProperty("apiVersion").GetString());
        Assert.Equal(
            new[] { "extendedLocation", "identity", "kind", "location", "name", "plan", "properties", "sku", "tags", "type", "zones" },
            details.GetProperty("resourceContent").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void BuildRequest_ApiVersionFromAfter_ThenTemplate_ThenSkip()
    {
        const string template = """
            {
              "resources": [
                { "type": "Microsoft.Network/virtualNetworks", "apiVersion": "2024-05-01", "name": "vnet1" },
                { "type": "microsoft.storage/STORAGEACCOUNTS", "apiVersion": "2023-05-01", "name": "sa1" },
                { "type": "Microsoft.Storage/storageAccounts", "apiVersion": "2022-09-01", "name": "sa2" }
              ]
            }
            """;
        var id = StorageId(1);

        var fromAfter = PolicyPreflight.BuildRequest(id, Change(id, "Microsoft.Storage/storageAccounts", "2025-01-01").AfterJson!, template, NoSecrets, out _);
        var fromTemplate = PolicyPreflight.BuildRequest(id, Change(id, "Microsoft.Storage/storageAccounts", apiVersion: null).AfterJson!, template, NoSecrets, out _);
        var missing = PolicyPreflight.BuildRequest(id, Change(id, "Microsoft.Storage/storageAccounts", apiVersion: null).AfterJson!, EmptyTemplate, NoSecrets, out var skip);
        var noTemplate = PolicyPreflight.BuildRequest(id, Change(id, "Microsoft.Storage/storageAccounts", apiVersion: null).AfterJson!, null, NoSecrets, out var skipNoTemplate);

        Assert.Equal("2025-01-01", JsonNode.Parse(fromAfter!)!["resourceDetails"]!["apiVersion"]!.GetValue<string>());
        Assert.Equal("2023-05-01", JsonNode.Parse(fromTemplate!)!["resourceDetails"]!["apiVersion"]!.GetValue<string>());
        Assert.Null(missing);
        Assert.False(string.IsNullOrWhiteSpace(skip));
        Assert.Null(noTemplate);
        Assert.False(string.IsNullOrWhiteSpace(skipNoTemplate));
    }

    [Fact]
    public void BuildRequest_ChildResource_SetsParentScope()
    {
        const string vnetId = "/subscriptions/00000000-0000-0000-0000-0000000000a1/resourcegroups/rg-app/providers/Microsoft.Network/virtualNetworks/vnet1";
        var subnet = Change($"{vnetId}/subnets/default", "Microsoft.Network/virtualNetworks/subnets", "2024-05-01",
            """{ "addressPrefix": "10.0.0.0/24" }""");
        var vnet = Change(vnetId, "Microsoft.Network/virtualNetworks", "2024-05-01");

        var subnetDetails = JsonNode.Parse(Build(subnet))!["resourceDetails"]!.AsObject();
        var vnetDetails = JsonNode.Parse(Build(vnet))!["resourceDetails"]!.AsObject();

        Assert.Equal(vnetId, subnetDetails["scope"]!.GetValue<string>());
        Assert.False(vnetDetails.ContainsKey("scope"));
    }

    // ---------- response parsing: verdicts from policyEvaluations only ----------

    [Fact]
    public void ParseResult_DenyFromEvaluation()
    {
        var id = StorageId(1);
        var json = Response(Evaluation("Deny", "baseline"), Evaluation("Deny", "baseline"));

        var finding = Assert.Single(PolicyPreflight.ParseResult(id, json)); // the duplicate is removed

        Assert.Equal(id, finding.ResourceId);
        Assert.Equal(PolicyVerdict.Deny, finding.Verdict);
        Assert.Equal("Deny", finding.Effect);
        Assert.Equal($"/subscriptions/{Sub}/providers/Microsoft.Authorization/policyAssignments/baseline", finding.PolicyAssignmentId);
        Assert.Equal("/providers/Microsoft.Authorization/policyDefinitions/4fa4b6c0-31ca-4c0d-b10d-24b96f62a751", finding.PolicyDefinitionId);
        Assert.Equal("storageDisallowPublicAccess", finding.PolicyDefinitionReferenceId);
        Assert.Equal("Storage accounts must not allow public blob access.", finding.Reason);
    }

    [Fact]
    public void ParseResult_AuditFromEvaluation()
    {
        var id = StorageId(1);
        const string json = """
            {
              "fieldRestrictions": [],
              "contentEvaluationResult": {
                "policyEvaluations": [
                  {
                    "policyInfo": { "policyDefinitionId": "/providers/Microsoft.Authorization/policyDefinitions/diag", "policyAssignmentId": "/subscriptions/s/providers/Microsoft.Authorization/policyAssignments/b-diag" },
                    "evaluationResult": "NonCompliant",
                    "effectDetails": { "policyEffect": "AuditIfNotExists" }
                  },
                  {
                    "policyInfo": { "policyDefinitionId": "/providers/Microsoft.Authorization/policyDefinitions/tls", "policyAssignmentId": "/subscriptions/s/providers/Microsoft.Authorization/policyAssignments/a-tls" },
                    "evaluationResult": "NonCompliant",
                    "evaluationDetails": {
                      "evaluatedExpressions": [
                        { "result": "True", "expressionKind": "Field", "expression": "Microsoft.Storage/storageAccounts/minimumTlsVersion",
                          "path": "properties.minimumTlsVersion", "expressionValue": "TLS1_0", "targetValue": "TLS1_2", "operator": "NotEquals" }
                      ]
                    },
                    "effectDetails": { "policyEffect": "audit" }
                  }
                ]
              }
            }
            """;

        var findings = PolicyPreflight.ParseResult(id, json);

        Assert.Equal(2, findings.Count);
        Assert.Equal(PolicyVerdict.Audit, findings[0].Verdict);
        Assert.Equal("audit", findings[0].Effect);
        Assert.Equal("properties.minimumTlsVersion NotEquals TLS1_2", findings[0].Reason);
        Assert.Null(findings[0].PolicyDefinitionReferenceId);
        Assert.Equal(PolicyVerdict.Other, findings[1].Verdict);
        Assert.Equal("AuditIfNotExists", findings[1].Effect);
        Assert.Equal("Policy evaluation reported a violation", findings[1].Reason);
    }

    [Theory]
    [InlineData("""
        {
          "fieldRestrictions": [
            { "field": "tags[costCenter]", "restrictions": [ { "result": "Required", "defaultValue": "cc-001",
              "policy": { "policyDefinitionId": "/providers/Microsoft.Authorization/policyDefinitions/tag", "policyAssignmentId": "/subscriptions/s/providers/Microsoft.Authorization/policyAssignments/tag" } } ] },
            { "field": "location", "restrictions": [ { "result": "Deny", "values": [ "westeurope", "northeurope" ], "policyEffect": "Deny", "reason": "Allowed locations",
              "policy": { "policyDefinitionId": "/providers/Microsoft.Authorization/policyDefinitions/loc", "policyAssignmentId": "/subscriptions/s/providers/Microsoft.Authorization/policyAssignments/loc" } } ] }
          ],
          "contentEvaluationResult": { "policyEvaluations": [] }
        }
        """)]
    [InlineData("""
        { "fieldRestrictions": [ { "field": "location", "restrictions": [ { "result": "Audit", "values": [ "westeurope" ],
          "policy": { "policyDefinitionId": "/providers/Microsoft.Authorization/policyDefinitions/loc", "policyAssignmentId": "/subscriptions/s/providers/Microsoft.Authorization/policyAssignments/loc" } } ] } ] }
        """)]
    public void ParseResult_FieldRestrictionsAlone_GiveNoFindings(string json)
    {
        Assert.Empty(PolicyPreflight.ParseResult(StorageId(1), json));
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Compliant")]
    public void ParseResult_EvaluationResultError_IsOther(string evaluationResult)
    {
        var finding = Assert.Single(PolicyPreflight.ParseResult(StorageId(1), Response(Evaluation("Deny", "baseline", evaluationResult))));

        Assert.Equal(PolicyVerdict.Other, finding.Verdict);
        Assert.Equal("Deny", finding.Effect);
        Assert.Equal($"evaluation result '{evaluationResult}'", finding.Reason);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("""{"contentEvaluationResult":{"policyEvaluations":{"not":"an array"}}}""")]
    public void ParseResult_MalformedResponse_Throws_NeverNoFindings(string json)
    {
        Assert.ThrowsAny<JsonException>(() => PolicyPreflight.ParseResult(StorageId(1), json));
    }

    // ---------- the engine ----------

    [Fact]
    public async Task Check_RgAndSubscriptionEndpoints_CaseInsensitive()
    {
        var handler = Answering(_ => Json(Response()));
        WhatIfChange[] changes =
        [
            Change("/subscriptions/SUB-A/resourcegroups/Rg-One/providers/Microsoft.Storage/storageAccounts/sa1", "Microsoft.Storage/storageAccounts"),
            Change("/Subscriptions/sub-b/resourceGroups/rg-new", "Microsoft.Resources/resourceGroups", "2024-03-01"),
            Change("/SUBSCRIPTIONS/sub-c/providers/Microsoft.Security/pricings/VirtualMachines", "Microsoft.Security/pricings", "2024-01-01"),
        ];

        var result = await Engine(handler).CheckAsync(changes, EmptyTemplate, null, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(3, result.Checked);
        Assert.Equal(0, result.NotChecked);
        Assert.Empty(result.Notes);
        Assert.Equal(
            new[]
            {
                "/subscriptions/SUB-A/resourceGroups/Rg-One/providers/Microsoft.PolicyInsights/checkPolicyRestrictions",
                "/subscriptions/sub-b/providers/Microsoft.PolicyInsights/checkPolicyRestrictions",
                "/subscriptions/sub-c/providers/Microsoft.PolicyInsights/checkPolicyRestrictions",
            },
            handler.Sent.Select(s => s.Path).OrderBy(p => p, StringComparer.Ordinal));
        Assert.All(handler.Sent, s =>
        {
            Assert.Equal("POST", s.Method);
            Assert.Equal("?api-version=2024-10-01", s.Query);
            using var body = JsonDocument.Parse(s.Body);
            Assert.True(body.RootElement.GetProperty("includeAuditEffect").GetBoolean());
            Assert.False(body.RootElement.TryGetProperty("pendingFields", out _));
            Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("resourceDetails").GetProperty("resourceContent").ValueKind);
        });
    }

    [Fact]
    public async Task Check_FindingsFromEveryResource_AreMergedAndOrdered()
    {
        var handler = Answering(s => Json(s.ContentName == "sa02"
            ? Response(Evaluation("Audit", "z-audit"), Evaluation("Deny", "a-deny"))
            : DenyResponse));

        var result = await Engine(handler).CheckAsync([Storage(2), Storage(1)], EmptyTemplate, null, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(2, result.Checked);
        Assert.Equal(
            new[] { (StorageId(1), PolicyVerdict.Deny), (StorageId(2), PolicyVerdict.Deny), (StorageId(2), PolicyVerdict.Audit) },
            result.Findings.Select(f => (f.ResourceId, f.Verdict)));
    }

    [Fact]
    public async Task Check_Forbidden_StopsWithOneNote()
    {
        var handler = Answering(_ => Forbidden());
        var changes = Enumerable.Range(1, 8).Select(Storage).ToList();

        var result = await Engine(handler).CheckAsync(changes, EmptyTemplate, null, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(ForbiddenNote, Assert.Single(result.Notes));
        Assert.Empty(result.Findings);
        Assert.Equal(0, result.Checked);
        Assert.Equal(8, result.NotChecked);
        Assert.True(handler.Sent.Count <= PolicyPreflight.MaxConcurrency, $"{handler.Sent.Count} calls after the first 403");
    }

    [Fact]
    public async Task Check_ForbiddenAfterFindings_KeepsThem()
    {
        // Ids are checked in order, so sa01-sa03 are always asked before any 403 can stop the run.
        var handler = Answering(s => s.ContentName is "sa01" or "sa02" or "sa03" ? Json(DenyResponse) : Forbidden());
        var changes = Enumerable.Range(1, 8).Select(Storage).ToList();

        var result = await Engine(handler).CheckAsync(changes, EmptyTemplate, null, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(3, result.Checked);
        Assert.Equal(5, result.NotChecked);
        Assert.Equal(Enumerable.Range(1, 3).Select(StorageId), result.Findings.Select(f => f.ResourceId));
        Assert.All(result.Findings, f => Assert.Equal(PolicyVerdict.Deny, f.Verdict));
        Assert.Equal(ForbiddenNote, Assert.Single(result.Notes));
        Assert.True(handler.Sent.Count <= 3 + PolicyPreflight.MaxConcurrency, $"{handler.Sent.Count} calls in all");
    }

    [Theory]
    [InlineData(500, """{"error":{"code":"InternalServerError","message":"boom"}}""", "InternalServerError")]
    [InlineData(400, """{"error":{"code":"InvalidRequestContent","message":"bad content"}}""", "InvalidRequestContent")]
    [InlineData(429, "", "Throttled")]
    [InlineData(200, "this is not json", "MalformedResponse")]
    public async Task Check_OneFailure_SkipsOnlyThatResource(int status, string failingBody, string code)
    {
        var handler = Answering(s => s.ContentName == "sa02" ? Json(failingBody, (HttpStatusCode)status) : Json(DenyResponse));

        var result = await Engine(handler).CheckAsync([Storage(1), Storage(2), Storage(3)], EmptyTemplate, null,
            TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(2, result.Checked);
        Assert.Equal(1, result.NotChecked);
        Assert.Equal([StorageId(1), StorageId(3)], result.Findings.Select(f => f.ResourceId));
        var note = Assert.Single(result.Notes);
        Assert.Contains(StorageId(2), note);
        Assert.Contains(code, note);
    }

    [Theory]
    [InlineData("network", "NetworkError")]
    [InlineData("httpTimeout", "Timeout")]
    public async Task Check_NetworkFailureOrHttpTimeout_SkipsOnlyThatResource(string failure, string code)
    {
        var handler = new PolicyHandler(async (sent, token) =>
        {
            if (sent.ContentName == "sa02")
            {
                if (failure == "network")
                    throw new HttpRequestException("No such host is known. (management.azure.com:443)");
                await Task.Delay(Timeout.Infinite, token); // only HttpClient.Timeout ends this one
            }
            return Json(DenyResponse);
        });
        var engine = Engine(handler, httpTimeout: failure == "httpTimeout" ? TimeSpan.FromMilliseconds(500) : null);

        var result = await engine.CheckAsync([Storage(1), Storage(2), Storage(3)], EmptyTemplate, null,
            TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(2, result.Checked);
        Assert.Equal(1, result.NotChecked);
        Assert.Equal([StorageId(1), StorageId(3)], result.Findings.Select(f => f.ResourceId));
        var note = Assert.Single(result.Notes);
        Assert.Contains(StorageId(2), note);
        Assert.Contains(code, note);
    }

    [Fact]
    public async Task Check_CapsAtMaxResources()
    {
        var handler = Answering(_ => Json(Response()));
        var changes = Enumerable.Range(0, 55).Reverse().Select(Storage).ToList(); // not in id order

        var result = await Engine(handler).CheckAsync(changes, EmptyTemplate, null, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(PolicyPreflight.MaxResources, handler.Sent.Count);
        Assert.Equal(
            Enumerable.Range(0, 50).Select(i => $"sa{i:D2}"),
            handler.Sent.Select(s => s.ContentName!).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(50, result.Checked);
        Assert.Equal(5, result.NotChecked);
        Assert.Equal("Only the first 50 resources were checked against Azure Policy; 5 more were not checked.", Assert.Single(result.Notes));
    }

    [Fact]
    public async Task Check_NeverMoreThanThreeInFlight()
    {
        var inFlight = 0;
        var max = 0;
        var gate = new object();
        var handler = new PolicyHandler(async (_, token) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            lock (gate) max = Math.Max(max, now);
            await Task.Delay(60, token);
            Interlocked.Decrement(ref inFlight);
            return Json(Response());
        });
        var changes = Enumerable.Range(1, 10).Select(Storage).ToList();

        var result = await Engine(handler).CheckAsync(changes, EmptyTemplate, null, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(10, result.Checked);
        Assert.Equal(PolicyPreflight.MaxConcurrency, max);
    }

    [Fact]
    public async Task Check_BudgetExpires_KeepsFindingsGathered()
    {
        var handler = new PolicyHandler(async (sent, token) =>
        {
            if (sent.ContentName == "sa01")
                await Task.Delay(Timeout.Infinite, token); // only the deadline ends this one
            return Json(DenyResponse);
        });
        var changes = Enumerable.Range(1, 6).Select(Storage).ToList();
        var clock = Stopwatch.StartNew();

        var result = await Engine(handler).CheckAsync(changes, EmptyTemplate, null, TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
        Assert.Equal(5, result.Checked);
        Assert.Equal(1, result.NotChecked);
        Assert.Equal(Enumerable.Range(2, 5).Select(StorageId), result.Findings.Select(f => f.ResourceId));
        Assert.Equal("Policy check stopped at the time limit: 1 of 6 resources not checked.", Assert.Single(result.Notes));
    }

    [Fact]
    public async Task Check_ZeroBudget_ChecksNothing_AndSaysSo()
    {
        var handler = Answering(_ => Json(DenyResponse));

        var result = await Engine(handler).CheckAsync([Storage(1), Storage(2)], EmptyTemplate, null, TimeSpan.Zero, CancellationToken.None);

        Assert.Empty(handler.Sent);
        Assert.Equal(0, result.Checked);
        Assert.Equal(2, result.NotChecked);
        Assert.Equal("Policy check stopped at the time limit: 2 of 2 resources not checked.", Assert.Single(result.Notes));
    }

    [Theory]
    [InlineData("not a template", null)]
    [InlineData("""{"parameters":{"p":{"type":"secureString"}},"resources":[]}""", "not parameters")]
    public async Task Check_UnreadableTemplateOrParameters_SendsNothing(string template, string? parameters)
    {
        var handler = Answering(_ => Json(DenyResponse));

        var result = await Engine(handler).CheckAsync([Storage(1)], template, parameters, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Empty(handler.Sent);
        Assert.Equal(0, result.Checked);
        Assert.Equal(1, result.NotChecked);
        Assert.Contains("secure parameters could not be read", Assert.Single(result.Notes));
    }

    [Fact]
    public async Task Check_DeleteAndNoChangeRows_AreIgnored()
    {
        var handler = Answering(_ => Json(DenyResponse));
        WhatIfChange[] changes =
        [
            new(WhatIfChangeKind.Delete, StorageId(1), """{"name":"sa01"}""", null, 0),
            Change(StorageId(2), "Microsoft.Storage/storageAccounts", kind: WhatIfChangeKind.NoChange),
            Change(StorageId(3), "Microsoft.Storage/storageAccounts", kind: WhatIfChangeKind.Ignore),
            new(WhatIfChangeKind.Create, StorageId(4), null, null, 0),
            new(WhatIfChangeKind.Modify, StorageId(5), "{}", "", 0),
        ];

        var result = await Engine(handler).CheckAsync(changes, EmptyTemplate, null, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Empty(handler.Sent);
        Assert.Empty(result.Findings);
        Assert.Empty(result.Notes);
        Assert.Equal(0, result.Checked);
        Assert.Equal(0, result.NotChecked);
    }

    [Fact]
    public async Task Check_CallerCancelled_Propagates()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new PolicyHandler(async (_, token) =>
        {
            arrived.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Json(DenyResponse);
        });
        using var cts = new CancellationTokenSource();

        var check = Engine(handler).CheckAsync([Storage(1), Storage(2)], EmptyTemplate, null, TimeSpan.FromMinutes(5), cts.Token);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public void Caveats_SayArmEnforcesPolicyAtDeployment()
    {
        Assert.Equal(5, PolicyPreflight.Caveats.Count);
        Assert.Contains("Azure Policy is enforced by ARM at deployment.", PolicyPreflight.Caveats);
        Assert.Contains(PolicyPreflight.Caveats, c => c.Contains("management groups", StringComparison.Ordinal));
        Assert.Contains(PolicyPreflight.Caveats, c => c.Contains("Secret values are removed", StringComparison.Ordinal));
    }
}

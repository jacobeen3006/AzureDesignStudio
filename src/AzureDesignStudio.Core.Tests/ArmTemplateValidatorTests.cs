using System.Collections.Generic;
using System.Linq;
using AzureDesignStudio.Discovery.Iac;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 plan 10.3 - in-process ARM validation: graph resolution, expressions, secret defaults.</summary>
public class ArmTemplateValidatorTests
{
    private static string Template(string resources, string parameters = "{}", string variables = "{}", string outputs = "{}") =>
        $$"""{ "contentVersion": "1.0.0.0", "parameters": {{parameters}}, "variables": {{variables}}, "resources": [{{resources}}], "outputs": {{outputs}} }""";

    private static bool Has(IReadOnlyList<ArmDiagnostic> diagnostics, string code, ArmDiagnosticSeverity? severity = null) =>
        diagnostics.Any(d => d.Code == code && (severity is null || d.Severity == severity));

    [Fact]
    public void ValidTemplate_ProducesNoDiagnostics()
    {
        var text = Template(
            """{"name":"st1","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","location":"[parameters('location')]","tags":{"v":"[variables('v')]"}},"""
            + """{"name":"vnet1","type":"Microsoft.Network/virtualNetworks","apiVersion":"2023-01-01","dependsOn":["st1"]}""",
            parameters: """{ "location": { "type": "string" } }""",
            variables: """{ "v": "prod" }""");

        Assert.Empty(ArmTemplateValidator.Validate(text));
    }

    [Fact]
    public void MissingApiVersionAndEmptyResources_AreErrors()
    {
        Assert.True(Has(ArmTemplateValidator.Validate(Template("""{"name":"x","type":"Microsoft.Storage/storageAccounts"}""")), "resource.api_version"));
        Assert.True(Has(ArmTemplateValidator.Validate("""{"contentVersion":"1.0.0.0","resources":[]}"""), "resources"));
    }

    [Fact]
    public void DuplicateNameAndUnknownDependency_AreCaught()
    {
        var text = Template(
            """{"name":"dup","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01"},"""
            + """{"name":"dup","type":"Microsoft.Network/virtualNetworks","apiVersion":"2023-01-01","dependsOn":["ghost"]}""");

        var diagnostics = ArmTemplateValidator.Validate(text);
        Assert.True(Has(diagnostics, "resource.duplicate"));
        Assert.True(Has(diagnostics, "dependency.unknown"));
    }

    [Fact]
    public void DependsOnCycle_IsNamedEndToEnd()
    {
        var text = Template(
            """{"name":"a","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","dependsOn":["b"]},"""
            + """{"name":"b","type":"Microsoft.Network/virtualNetworks","apiVersion":"2023-01-01","dependsOn":["a"]}""");

        var cycle = ArmTemplateValidator.Validate(text).Single(d => d.Code == "dependency.cycle");
        Assert.Contains("a -> b -> a", cycle.Message);
    }

    [Fact]
    public void UndeclaredReferences_Error_WhileUnusedParameterWarns()
    {
        var text = Template(
            """{"name":"a","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","sku":"[parameters('ghost')]","zone":"[variables('novar')]"}""",
            parameters: """{ "ghost": { "type": "string" }, "spare": { "type": "string" } }""");

        var diagnostics = ArmTemplateValidator.Validate(text);
        Assert.False(Has(diagnostics, "expression.parameter")); // ghost IS declared
        Assert.True(Has(diagnostics, "expression.variable"));
        Assert.True(Has(diagnostics, "parameter.unused", ArmDiagnosticSeverity.Warning));
    }

    [Fact]
    public void SecureParameterWithLiteralDefault_IsRejected()
    {
        var text = Template(
            """{"name":"sql1","type":"Microsoft.Sql/servers","apiVersion":"2023-01-01","properties":{"administratorLoginPassword":"[parameters('adminPassword')]"}}""",
            parameters: """{ "adminPassword": { "type": "secureString", "defaultValue": "hunter2" } }""");

        var diagnostics = ArmTemplateValidator.Validate(text);
        Assert.True(Has(diagnostics, "parameter.secret_default", ArmDiagnosticSeverity.Error));
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("hunter2")); // the finding never echoes the secret itself
    }

    [Fact]
    public void MalformedJson_AndBracketLiterals_Behave()
    {
        Assert.Single(ArmTemplateValidator.Validate("not json"));

        var escaped = Template("""{"name":"a","type":"Microsoft.Storage/storageAccounts","apiVersion":"2023-01-01","note":"[[parameters('x')]"}""");
        Assert.DoesNotContain(ArmTemplateValidator.Validate(escaped), d => d.Code.StartsWith("expression."));
    }
}

using AzureDesignStudio.Discovery.Iac;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>v2 plan 10.3 - .bicepparam text: Key Vault references for secrets, typed literals, honest missing-value notes.</summary>
public class BicepParamsTextTests
{
    [Fact]
    public void SecretParameter_EmitsKeyVaultReference_AndNeverPlaintext()
    {
        var text = BicepParamsText.Generate(
            "./main.bicep",
            [new BicepParameter("adminPassword", "secureString", IsSecret: true)],
            new KeyVaultPointer("kv-prod", "sub-1", "rg-security"),
            new System.Collections.Generic.Dictionary<string, string> { ["adminPassword"] = "P@ssw0rd!" });

        Assert.Contains("keyVault: {", text);
        Assert.Contains("name: 'kv-prod'", text);
        Assert.Contains("secretName: 'ads-adminpassword'", text);
        Assert.Contains("vaultSubscriptionId: 'sub-1'", text);
        Assert.Contains("ignored by design", text);
        Assert.DoesNotContain("P@ssw0rd!", text); // the hard rule
    }

    [Fact]
    public void SecretWithoutVault_IsFlaggedNotGuessed()
    {
        var text = BicepParamsText.Generate("./main.bicep", [new BicepParameter("adminPassword", "secureString", IsSecret: true)]);

        Assert.Contains("// MISSING KEY VAULT for secret parameter 'adminPassword'", text);
        Assert.DoesNotContain("param adminPassword", text);
    }

    [Fact]
    public void Literals_AreTypedPerBicep()
    {
        var text = BicepParamsText.Generate(
            "./main.bicep",
            [
                new BicepParameter("location", "string"),
                new BicepParameter("replica", "bool"),
                new BicepParameter("count", "int"),
                new BicepParameter("settings", "object"),
                new BicepParameter("greeting", "string"),
            ],
            values: new System.Collections.Generic.Dictionary<string, string>
            {
                ["location"] = "westeurope",
                ["replica"] = "true",
                ["count"] = "3",
                ["settings"] = """{"a":1}""",
                ["greeting"] = "it's here",
            });

        Assert.Contains("param location = 'westeurope'", text);
        Assert.Contains("param replica = true", text);
        Assert.Contains("param count = 3", text);
        Assert.Contains("""param settings = {"a":1}""", text);
        Assert.Contains(@"param greeting = 'it\'s here'", text);
    }

    [Fact]
    public void MissingRequired_IsCommented_OptionalOmitted_UsingLinePresent()
    {
        var text = BicepParamsText.Generate(
            "./main.bicep",
            [new BicepParameter("size", "string"), new BicepParameter("tier", "string", Required: false)]);

        Assert.Contains("using './main.bicep'", text);
        Assert.Contains("// MISSING VALUE for required parameter 'size'", text);
        Assert.DoesNotContain("param tier", text);
    }
}

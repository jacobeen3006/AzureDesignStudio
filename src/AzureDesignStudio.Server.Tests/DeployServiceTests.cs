using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.Server.Services;
using AzureDesignStudio.Services;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureDesignStudio.Server.Tests;

/// <summary>The Deploy RPCs that answer without calling Azure: linking a subscription and
/// the guardrail policy. Everything past them needs ARM and is not covered here.</summary>
public class DeployServiceTests
{
    private readonly DesignDbContext _db = TestData.NewDb();
    private readonly FakeCryptoService _crypto = new();
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _subscription = Guid.NewGuid();

    private DeployService Service => new(_db, NullLogger<DeployService>.Instance, _crypto,
        new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl), new ScriptedHttp(_ => null));

    private SubscriptionInfo Link(string secret = "s3cret", string name = "Production") => new()
    {
        SubscriptionId = _subscription.ToString(),
        SubscriptionName = name,
        TenantId = Guid.NewGuid().ToString(),
        ClientId = Guid.NewGuid().ToString(),
        ClientSecret = secret,
    };

    // --- linking a subscription ---

    [Fact]
    public async Task LinkedSubscriptionIsStoredEncrypted()
    {
        var response = await Service.SaveSubscriptionInfo(Link("s3cret"), TestServerCallContext.ForUser(_user));

        Assert.Equal(200, response.StatusCode);
        var row = Assert.Single(_db.AzureSubscriptions);
        Assert.Equal("enc:s3cret", row.ClientSecret);
        Assert.Equal(_user, row.UserId);
    }

    [Fact]
    public async Task LinkingTheSameSubscriptionAgainReplacesTheLink()
    {
        // A rotated secret used to add a second row while lookups kept returning the first.
        var me = TestServerCallContext.ForUser(_user);
        await Service.SaveSubscriptionInfo(Link("old", "Production"), me);

        var response = await Service.SaveSubscriptionInfo(Link("rotated", "Production EU"), me);

        Assert.Equal(200, response.StatusCode);
        var row = Assert.Single(_db.AzureSubscriptions);
        Assert.Equal("enc:rotated", row.ClientSecret);
        Assert.Equal("Production EU", row.SubscriptionName);
    }

    [Fact]
    public async Task StoreThatCannotEncryptIsTheServersFaultNotTheCallers()
    {
        // Key Vault not configured: 400 told the user to correct details that were fine.
        _crypto.EncryptFailure = new InvalidOperationException("Key Vault is not configured");

        var response = await Service.SaveSubscriptionInfo(Link(), TestServerCallContext.ForUser(_user));

        Assert.Equal(503, response.StatusCode);
        Assert.Empty(_db.AzureSubscriptions);
    }

    [Fact]
    public async Task MalformedIdIsABadRequest()
    {
        var request = Link();
        request.TenantId = "not-a-guid";

        var response = await Service.SaveSubscriptionInfo(request, TestServerCallContext.ForUser(_user));

        Assert.Equal(400, response.StatusCode);
        Assert.Empty(_db.AzureSubscriptions);
    }

    [Fact]
    public async Task SecretTooLongForTheKeyIsABadRequest()
    {
        _crypto.EncryptFailure = new ArgumentException("The secret is 600 bytes; the configured key can encrypt at most 214.");

        Assert.Equal(400, (await Service.SaveSubscriptionInfo(Link(), TestServerCallContext.ForUser(_user))).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad<name>")]
    [InlineData("semi;colon")]
    public async Task InvalidSubscriptionNameIsABadRequest(string name)
    {
        Assert.Equal(400, (await Service.SaveSubscriptionInfo(Link(name: name), TestServerCallContext.ForUser(_user))).StatusCode);
    }

    [Fact]
    public async Task LinkingNeedsAnIdentity()
    {
        Assert.Equal(401, (await Service.SaveSubscriptionInfo(Link(), TestServerCallContext.WithoutIdentity())).StatusCode);
        Assert.Equal(401, (await Service.LoadSubscriptionInfo(new Empty(), TestServerCallContext.WithoutIdentity())).StatusCode);
    }

    [Fact]
    public async Task ListShowsEachSubscriptionOnceAndOnlyTheCallersOwn()
    {
        // Rows saved before links were upserted can repeat a subscription; the newest wins.
        _db.AzureSubscriptions.AddRange(
            new AzureSubscriptionModel { UserId = _user, SubscriptionId = _subscription, SubscriptionName = "old name", TenantId = Guid.NewGuid(), ClientId = Guid.NewGuid(), ClientSecret = "enc:a" },
            new AzureSubscriptionModel { UserId = _user, SubscriptionId = _subscription, SubscriptionName = "new name", TenantId = Guid.NewGuid(), ClientId = Guid.NewGuid(), ClientSecret = "enc:b" },
            new AzureSubscriptionModel { UserId = Guid.NewGuid(), SubscriptionId = Guid.NewGuid(), SubscriptionName = "someone else", TenantId = Guid.NewGuid(), ClientId = Guid.NewGuid(), ClientSecret = "enc:c" });
        await _db.SaveChangesAsync();

        var response = await Service.LoadSubscriptionInfo(new Empty(), TestServerCallContext.ForUser(_user));

        Assert.Equal(200, response.StatusCode);
        var listed = Assert.Single(response.SubscriptionData);
        Assert.Equal("new name", listed.SubscriptionName);
        Assert.Equal(_subscription.ToString(), listed.SubscriptionId);
    }

    // --- a link that can no longer be read ---

    [Fact]
    public async Task LinkWhoseSecretCannotBeDecryptedReadsAsNotLinked()
    {
        // An unreadable link failed the whole RPC; it now answers "no subscription" so the
        // user can link it again.
        await Service.SaveSubscriptionInfo(Link(), TestServerCallContext.ForUser(_user));
        _crypto.DecryptFailure = new FormatException("not base64");

        var response = await Service.GetResourceGroups(
            new GetRgsRequest { SubscriptionId = _subscription.ToString() }, TestServerCallContext.ForUser(_user));

        Assert.Equal(404, response.StatusCode);
        Assert.Empty(response.ResourceGroupName);
    }

    [Fact]
    public async Task ResourceGroupsOfASubscriptionThatIsNotLinkedAnswersNotFound()
    {
        var response = await Service.GetResourceGroups(
            new GetRgsRequest { SubscriptionId = Guid.NewGuid().ToString() }, TestServerCallContext.ForUser(_user));

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task ResourceGroupsWithAMalformedSubscriptionIdIsABadRequest()
    {
        var response = await Service.GetResourceGroups(
            new GetRgsRequest { SubscriptionId = "nope" }, TestServerCallContext.ForUser(_user));

        Assert.Equal(400, response.StatusCode);
    }

    // --- guardrail policy ---

    [Fact]
    public async Task GuardrailPolicyRoundTripsAndRecordsWhoSetIt()
    {
        const string policy = """{"scopes":["/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/prod"],"tags":[],"types":[]}""";

        var set = await Service.SetGuardrailPolicy(new GuardrailPolicyRequest { Json = policy }, TestServerCallContext.ForUser(_user, "Ada"));
        var get = await Service.GetGuardrailPolicy(new Empty(), TestServerCallContext.ForUser(Guid.NewGuid()));

        Assert.Equal(200, set.StatusCode);
        Assert.Equal(200, get.StatusCode);
        Assert.Equal(policy, get.Json);
        Assert.Equal($"Ada <{_user}>", get.UpdatedBy);
        Assert.Single(_db.AdsGuardrailPolicies);
    }

    [Fact]
    public async Task SettingThePolicyAgainReplacesTheSingleRow()
    {
        var me = TestServerCallContext.ForUser(_user);
        await Service.SetGuardrailPolicy(new GuardrailPolicyRequest { Json = """{"types":["Microsoft.KeyVault/vaults"]}""" }, me);

        await Service.SetGuardrailPolicy(new GuardrailPolicyRequest { Json = """{"types":["Microsoft.Sql/servers"]}""" }, me);

        Assert.Contains("Microsoft.Sql/servers", Assert.Single(_db.AdsGuardrailPolicies).Json);
    }

    [Fact]
    public async Task MalformedPolicyIsRejectedAndTheStoredOneKept()
    {
        // An emptied list means "protect nothing", so bad input must never overwrite it.
        var me = TestServerCallContext.ForUser(_user);
        await Service.SetGuardrailPolicy(new GuardrailPolicyRequest { Json = """{"types":["Microsoft.KeyVault/vaults"]}""" }, me);

        var response = await Service.SetGuardrailPolicy(new GuardrailPolicyRequest { Json = "{ not json" }, me);

        Assert.Equal(400, response.StatusCode);
        Assert.StartsWith("GUARDRAIL_POLICY_MALFORMED", response.ErrorMessage);
        Assert.Contains("Microsoft.KeyVault/vaults", Assert.Single(_db.AdsGuardrailPolicies).Json);
    }

    [Fact]
    public async Task GuardrailPolicyNeedsAnIdentity()
    {
        Assert.Equal(401, (await Service.GetGuardrailPolicy(new Empty(), TestServerCallContext.WithoutIdentity())).StatusCode);
        Assert.Equal(401, (await Service.SetGuardrailPolicy(new GuardrailPolicyRequest { Json = "{}" }, TestServerCallContext.WithoutIdentity())).StatusCode);
        Assert.Empty(_db.AdsGuardrailPolicies);
    }
}

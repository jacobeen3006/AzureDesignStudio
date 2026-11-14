using System.Security.Claims;
using Azure.Core;
using Azure.Identity;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Services;
using AzureDesignStudio.Services.InProcess;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureDesignStudio.Server.Tests;

// The desktop app has no server: the same service classes run in its process, reached through
// generated gRPC clients on InProcessCallInvoker. These tests build that arrangement the way
// the desktop host does (minus MAUI) and drive it through the clients and wrappers.

file sealed class AmbientUser(IReadOnlyList<AmbientSubscription> subscriptions, TokenCredential? credential = null) : IAmbientAzureCredential
{
    public TokenCredential Credential { get; } = credential ?? new RefusingCredential();

    public Task<IReadOnlyList<AmbientSubscription>> ListSubscriptionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(subscriptions);
}

/// <summary>A credential that proves it was asked: Azure is never reached in these tests.</summary>
file sealed class RefusingCredential : TokenCredential
{
    public int Requests { get; private set; }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Requests++;
        throw new AuthenticationFailedException("test credential");
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Requests++;
        throw new AuthenticationFailedException("test credential");
    }
}

public class InProcessTests
{
    private static readonly Guid User = Guid.NewGuid();

    private static ClaimsPrincipal SignedIn(Guid oid) =>
        new(new ClaimsIdentity(
            [new Claim("oid", oid.ToString()), new Claim("tid", TestTenants.Contoso.ToString()), new Claim("name", "Ada")],
            "test"));

    /// <summary>The desktop host's wiring, minus MAUI: services in DI, the invoker in front.</summary>
    private static (ServiceProvider Services, InProcessCallInvoker Invoker) Host(
        ClaimsPrincipal? caller = null, IAmbientAzureCredential? ambient = null, ScriptedHttp? http = null)
    {
        var databaseName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => TestData.NewDb(databaseName));
        services.AddSingleton<ICryptoService>(new FakeCryptoService());
        services.AddSingleton(new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl));
        services.AddSingleton<IHttpClientFactory>(http ?? new ScriptedHttp(_ => null));
        services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
        services.AddSingleton<Core.Common.IDesignOfflineStore, MemoryOfflineStore>();
        if (ambient is not null)
            services.AddSingleton(ambient);
        services.AddScoped<Design.DesignBase, LocalDesignService>();
        services.AddScoped<Deploy.DeployBase, DeployService>();
        services.AddScoped<Cost.CostBase, RetailPriceService>();
        var provider = services.BuildServiceProvider();

        var who = caller ?? new ClaimsPrincipal(new ClaimsIdentity());
        var invoker = new InProcessCallInvoker(provider, _ => ValueTask.FromResult(who))
            .Add<Design.DesignBase>(Design.Descriptor.FullName, Design.BindService)
            .Add<Deploy.DeployBase>(Deploy.Descriptor.FullName, Deploy.BindService)
            .Add<Cost.CostBase>(Cost.Descriptor.FullName, Cost.BindService);
        return (provider, invoker);
    }

    // --- designs: local, no sign-in, through the same wrapper the drawers use ---

    [Fact]
    public async Task DesignsSaveListLoadAndDeleteWithoutSignIn()
    {
        var (services, invoker) = Host();
        var designs = new AzureDesignStudio.Services.DesignGrpcService(new FixedClientFactory(new Design.DesignClient(invoker)), []);

        Assert.Equal(200, await designs.SaveDesign("hub spoke", """{"Nodes":[]}"""));
        Assert.Equal(200, await designs.SaveDesign("hub spoke", """{"Nodes":[1]}""")); // replace
        var (listStatus, names) = await designs.GetAllSavedDesign();
        Assert.Equal(200, listStatus);
        Assert.Equal(["hub spoke"], names!);
        Assert.Equal((200, """{"Nodes":[1]}"""), await designs.LoadDesign("hub spoke"));
        Assert.Equal(200, await designs.DeleteDesign("hub spoke"));
        Assert.Equal(404, await designs.DeleteDesign("hub spoke"));
        Assert.Equal(404, (await designs.LoadDesign("hub spoke")).Item1);

        var store = (MemoryOfflineStore)services.GetRequiredService<Core.Common.IDesignOfflineStore>();
        Assert.Empty(store.Pending);
    }

    [Fact]
    public async Task LocalDesignsHaveNoTenDesignQuota()
    {
        var (_, invoker) = Host();
        var designs = new AzureDesignStudio.Services.DesignGrpcService(new FixedClientFactory(new Design.DesignClient(invoker)), []);

        for (var i = 0; i < 15; i++)
            Assert.Equal(200, await designs.SaveDesign($"design {i}", "{}"));

        Assert.Equal(15, (await designs.GetAllSavedDesign()).Item2!.Count);
    }

    [Fact]
    public async Task DesignThatIsNotJsonOrHasABadNameIsRefused()
    {
        var (_, invoker) = Host();
        var client = new Design.DesignClient(invoker);

        Assert.Equal(400, (await client.SaveAsync(new SaveDesignRequest { Name = "x", Data = "not json" })).StatusCode);
        Assert.Equal(400, (await client.SaveAsync(new SaveDesignRequest { Name = "../x", Data = "{}" })).StatusCode);
    }

    // --- the invoker itself ---

    [Fact]
    public async Task CallerReachesTheServiceAsTheSignedInUser()
    {
        var (_, anonymous) = Host();
        Assert.Equal(401, (await new Deploy.DeployClient(anonymous).LoadSubscriptionInfoAsync(new Empty())).StatusCode);

        var (_, signedIn) = Host(SignedIn(User));
        Assert.Equal(200, (await new Deploy.DeployClient(signedIn).LoadSubscriptionInfoAsync(new Empty())).StatusCode);
    }

    [Fact]
    public void InProcessCallsCarryTheSingleUserMarker()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", User.ToString()), new Claim("tid", TestTenants.Contoso.ToString())], "test"));
        var context = new InProcessServerCallContext("/test", principal);

        Assert.Equal(true, context.UserState[ServiceTools.SingleUserHostKey]);
        Assert.True(ServiceTools.TryGetCaller(context, out var caller, out _));
        Assert.True(caller.IsSingleUserHost);
        Assert.True(caller.CanAdministerGuardrails);
        Assert.True(caller.CanApprove);
    }

    [Fact]
    public void AServerStyleContextIsNotASingleUserHost()
    {
        // The server's interceptor puts the principal and nothing else in UserState.
        var context = TestServerCallContext.ForUser(User);

        Assert.False(context.UserState.ContainsKey(ServiceTools.SingleUserHostKey));
        Assert.True(ServiceTools.TryGetCaller(context, out var caller, out _));
        Assert.False(caller.IsSingleUserHost);
    }

    [Fact]
    public async Task ServerStreamingCallDeliversItsMessages()
    {
        var (_, invoker) = Host(SignedIn(User));
        using var call = new Deploy.DeployClient(invoker).CreateDeployment(new DeploymentRequest { ArmTemplate = "" });

        var messages = new List<DeploymentResponse>();
        while (await call.ResponseStream.MoveNext(CancellationToken.None))
            messages.Add(call.ResponseStream.Current);

        var only = Assert.Single(messages);
        Assert.Equal(400, only.StatusCode);
        Assert.Equal("Template is empty.", only.ErrorMessage);
    }

    [Fact]
    public async Task ExceptionInAServiceReachesTheClientAsAnRpcException()
    {
        // The wire's contract, which the client wrappers' catch filters rely on.
        var (_, invoker) = Host(SignedIn(User), http: new ScriptedHttp(_ => throw new InvalidOperationException("boom")));
        var failing = new Cost.CostClient(new InProcessCallInvoker(
            new ServiceCollection().AddScoped<Cost.CostBase, ThrowingCost>().BuildServiceProvider(),
            _ => ValueTask.FromResult(new ClaimsPrincipal()))
            .Add<Cost.CostBase>(Cost.Descriptor.FullName, Cost.BindService));

        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await failing.GetRetailPricesAsync(new RetailPriceRequest { ServiceName = "x", ArmRegionName = "y" }));

        Assert.Equal(StatusCode.Unknown, error.StatusCode);
        Assert.Contains("boom", error.Status.Detail);
    }

    private sealed class ThrowingCost : Cost.CostBase
    {
        public override Task<RetailPriceResponse> GetRetailPrices(RetailPriceRequest request, ServerCallContext context) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task ServiceThatIsNotRegisteredIsUnimplemented()
    {
        var invoker = new InProcessCallInvoker(new ServiceCollection().BuildServiceProvider(), _ => ValueTask.FromResult(new ClaimsPrincipal()));

        var error = await Assert.ThrowsAsync<RpcException>(async () => await new Cost.CostClient(invoker).GetRetailPricesAsync(new RetailPriceRequest()));

        Assert.Equal(StatusCode.Unimplemented, error.StatusCode);
    }

    [Fact]
    public async Task EachCallGetsItsOwnScope()
    {
        // A fresh DbContext per call, as a request has on the server: a design saved by one
        // call is read back by the next through the store, not a shared tracked entity.
        var (_, invoker) = Host(SignedIn(User));
        var client = new Deploy.DeployClient(invoker);

        var set = await client.SetGuardrailPolicyAsync(new GuardrailPolicyRequest { Json = """{"types":["Microsoft.KeyVault/vaults"]}""" });
        var get = await client.GetGuardrailPolicyAsync(new Empty());

        Assert.Equal(200, set.StatusCode);
        Assert.Contains("Microsoft.KeyVault/vaults", get.Json);
        Assert.Equal("Ada <" + User + ">", get.UpdatedBy);
    }

    [Fact]
    public async Task TheSingleUserCallerSetsBothTenantSettingsAndReadsThemBack()
    {
        // The in-process invoker marks its caller as the single-user host, which implies the
        // administrator rights (D-04): no Guardrail.Admin role is needed on the desktop.
        var (_, invoker) = Host(SignedIn(User));
        var client = new Deploy.DeployClient(invoker);

        var before = await client.GetTenantSettingsAsync(new Empty());
        var set = await client.SetTenantSettingsAsync(new TenantSettingsRequest { AllowSelfApproval = true, GuardrailPolicyMode = "merge" });
        var after = await client.GetTenantSettingsAsync(new Empty());

        Assert.Equal(200, before.StatusCode);
        Assert.False(before.AllowSelfApproval);
        Assert.Equal("replace", before.GuardrailPolicyMode);
        Assert.Equal(200, set.StatusCode);
        Assert.True(after.AllowSelfApproval);
        Assert.Equal("merge", after.GuardrailPolicyMode);
        Assert.Equal("Ada <" + User + ">", after.UpdatedBy);
    }

    [Fact]
    public async Task TheModeSetThroughTheGeneratedClientGovernsTheNextEvaluation()
    {
        // The default policy is operator-seeded data, so seed it through the host's own context;
        // the single-user caller then adds an own policy and switches the scope to merge.
        var (services, invoker) = Host(SignedIn(User));
        var client = new Deploy.DeployClient(invoker);
        await client.SetGuardrailPolicyAsync(new GuardrailPolicyRequest { Json = """{"types":["Microsoft.KeyVault/vaults"]}""" });
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DesignDbContext>();
            db.AdsGuardrailPolicies.Add(new GuardrailPolicyModel
            {
                Id = 1, TenantId = null, Json = """{"types":["Microsoft.Network/virtualNetworks"]}""",
                UpdatedBy = "operator", UpdatedByUserId = Guid.Empty.ToString("D"), UpdatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        async Task<string[]> Effective()
        {
            using var scope = services.CreateScope();
            var service = (DeployService)scope.ServiceProvider.GetRequiredService<Deploy.DeployBase>();
            var (policy, error) = await service.LoadGuardrailPolicy(TestTenants.Contoso, CancellationToken.None);
            Assert.Null(error);
            return [.. policy!.ProtectedTypes];
        }

        Assert.Equal(["Microsoft.KeyVault/vaults"], await Effective());
        await client.SetTenantSettingsAsync(new TenantSettingsRequest { GuardrailPolicyMode = "merge" });
        Assert.Equal(["Microsoft.Network/virtualNetworks", "Microsoft.KeyVault/vaults"], await Effective());
    }

    // --- deploying as the signed-in user, or as a linked service principal ---

    [Fact]
    public async Task SubscriptionsAreTheLinkedOnesPlusTheUsersOwn()
    {
        var linked = Guid.NewGuid();
        var mine = Guid.NewGuid().ToString();
        var ambient = new AmbientUser([new(linked.ToString(), "Production (as seen by me)"), new(mine, "My dev subscription")]);
        var (_, invoker) = Host(SignedIn(User), ambient);
        var client = new Deploy.DeployClient(invoker);
        await client.SaveSubscriptionInfoAsync(new SubscriptionInfo
        {
            SubscriptionId = linked.ToString(), SubscriptionName = "Production", TenantId = Guid.NewGuid().ToString(),
            ClientId = Guid.NewGuid().ToString(), ClientSecret = "s3cret",
        });

        var listed = await client.LoadSubscriptionInfoAsync(new Empty());

        Assert.Equal(new[] { ("Production", linked.ToString()), ("My dev subscription", mine) },
            listed.SubscriptionData.Select(s => (s.SubscriptionName, s.SubscriptionId)));
    }

    [Fact]
    public async Task UnlinkedSubscriptionIsReachedAsTheSignedInUser()
    {
        // No link: without an ambient identity this was "no subscription" (404). With one, the
        // call goes to Azure with the user's credential (here: refused, so 502 - not 404).
        var credential = new RefusingCredential();
        var (_, invoker) = Host(SignedIn(User), new AmbientUser([], credential));

        var response = await new Deploy.DeployClient(invoker).GetResourceGroupsAsync(new GetRgsRequest { SubscriptionId = Guid.NewGuid().ToString() });

        Assert.Equal(502, response.StatusCode);
        Assert.True(credential.Requests > 0);
    }

    [Fact]
    public async Task BrokenLinkIsNotSilentlyReplacedByTheUsersIdentity()
    {
        var subscription = Guid.NewGuid();
        var credential = new RefusingCredential();
        var (services, invoker) = Host(SignedIn(User), new AmbientUser([], credential));
        var client = new Deploy.DeployClient(invoker);
        await client.SaveSubscriptionInfoAsync(new SubscriptionInfo
        {
            SubscriptionId = subscription.ToString(), SubscriptionName = "Production", TenantId = Guid.NewGuid().ToString(),
            ClientId = Guid.NewGuid().ToString(), ClientSecret = "s3cret",
        });
        ((FakeCryptoService)services.GetRequiredService<ICryptoService>()).DecryptFailure = new InvalidOperationException("secret gone");

        var response = await client.GetResourceGroupsAsync(new GetRgsRequest { SubscriptionId = subscription.ToString() });

        Assert.Equal(404, response.StatusCode); // "link it again" - not a deployment as somebody else
        Assert.Equal(0, credential.Requests);
    }

    [Fact]
    public async Task PricesAreFetchedInProcess()
    {
        var http = new ScriptedHttp(_ => """{"Items":[{"serviceName":"Virtual Machines","skuName":"D2s v6","meterName":"D2s v6","retailPrice":0.1,"currencyCode":"USD","armRegionName":"westeurope"}]}""");
        var (_, invoker) = Host(http: http);

        var response = await new Cost.CostClient(invoker).GetRetailPricesAsync(new RetailPriceRequest { ServiceName = "Virtual Machines", ArmRegionName = "westeurope" });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(0.1, Assert.Single(response.Prices).RetailPrice);
    }
}

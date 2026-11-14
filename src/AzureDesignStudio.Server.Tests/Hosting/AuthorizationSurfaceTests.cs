using System.Net;
using System.Reflection;
using AzureDesignStudio.Server.Auth;
using AzureDesignStudio.Services;
using AzureDesignStudio.SharedModels.Protos;
using ProtoServiceDescriptor = Google.Protobuf.Reflection.ServiceDescriptor;
using Google.Protobuf.WellKnownTypes;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>
/// The host denies by default (a FallbackPolicy) and exposes exactly one anonymous gRPC method,
/// Cost.GetRetailPrices. The anonymous list is written as a literal in this file on purpose:
/// changing the production list must break the test.
/// </summary>
public sealed class AuthorizationSurfaceTests(AdsServerFactory factory) : IClassFixture<AdsServerFactory>
{
    /// <summary>
    /// The only gRPC method callable without authentication (D-03). A literal, not a reference
    /// to production code, so widening the production list breaks these tests.
    /// </summary>
    private static readonly string[] AnonymousMethods = ["/Cost/GetRetailPrices"];

    private static readonly Marshaller<byte[]> PassThrough = Marshallers.Create(b => b, b => b);

    private sealed record GrpcEndpoint(string Path, Method<byte[], byte[]> Method, Endpoint Endpoint)
    {
        public bool IsAnonymous => Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        public bool IsProtected => Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0;
    }

    private static GrpcEndpoint[] GrpcEndpoints(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(e => (Endpoint: e, Meta: e.Metadata.GetMetadata<GrpcMethodMetadata>()))
            .Where(x => x.Meta is not null)
            .Select(x => new GrpcEndpoint(
                $"/{x.Meta!.Method.ServiceName}/{x.Meta.Method.Name}",
                new Method<byte[], byte[]>(
                    x.Meta.Method.Type, x.Meta.Method.ServiceName, x.Meta.Method.Name, PassThrough, PassThrough),
                x.Endpoint))
            .ToArray();

    /// <summary>The Testing host (the class fixture) or a Development-environment host that also maps reflection.</summary>
    private HostLease Host(string environment) =>
        environment == "Development"
            ? new HostLease(factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development")), owned: true)
            : new HostLease(factory, owned: false);

    /// <summary>Disposes a host this test created; the shared class fixture is left alone.</summary>
    private sealed class HostLease(WebApplicationFactory<Program> host, bool owned) : IDisposable
    {
        public WebApplicationFactory<Program> Host => host;

        public void Dispose()
        {
            if (owned)
                host.Dispose();
        }
    }

    public static TheoryData<string> Environments => new() { "Testing", "Development" };

    // --- tracer ---

    [Fact]
    public void TheHostHasAnAuthenticatedUserFallbackPolicy()
    {
        var policy = factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy;

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements,
            r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task AnAnonymousDesignCallIsUnauthenticated()
    {
        using var channel = factory.CreateGrpcChannel();

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => new Design.DesignClient(channel).GetSavedAsync(new Empty()).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task AnAnonymousCostCallReachesTheService()
    {
        using var channel = factory.CreateGrpcChannel();

        var prices = await new Cost.CostClient(channel).GetRetailPricesAsync(new RetailPriceRequest());

        // An empty request is refused by the service itself, before any call to the price API.
        Assert.Equal(400, prices.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    public async Task TheSpaIsNotChallengedForAnAnonymousBrowser(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- the whole endpoint surface ---

    [Theory]
    [MemberData(nameof(Environments))]
    public void EveryGrpcEndpointIsProtectedExceptTheLiteralAnonymousList(string environment)
    {
        using var host = Host(environment);
        var endpoints = GrpcEndpoints(host.Host);

        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints)
        {
            if (AnonymousMethods.Contains(endpoint.Path))
            {
                Assert.True(endpoint.IsAnonymous, $"{endpoint.Path} must be anonymous");
            }
            else
            {
                Assert.True(endpoint.IsProtected, $"{endpoint.Path} carries no authorization metadata");
                Assert.False(endpoint.IsAnonymous, $"{endpoint.Path} is anonymous but not on the list");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public void TheAnonymousEndpointsEqualTheLiteralListExactly(string environment)
    {
        using var host = Host(environment);

        var anonymous = GrpcEndpoints(host.Host).Where(e => e.IsAnonymous).Select(e => e.Path).Order().ToArray();

        Assert.Equal(AnonymousMethods.Order().ToArray(), anonymous);
    }

    [Fact]
    public void TheDevelopmentHostMapsReflectionAndEveryReflectionEndpointRequiresAuthorization()
    {
        using var host = Host("Development");

        var reflection = GrpcEndpoints(host.Host)
            .Where(e => e.Method.ServiceName.Contains("Reflection", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(reflection);
        Assert.All(reflection, e =>
        {
            Assert.True(e.IsProtected, $"{e.Path} carries no authorization metadata");
            Assert.False(e.IsAnonymous, $"{e.Path} is anonymous");
        });
    }

    [Fact]
    public void EveryServiceOfTheSharedModelsAssemblyHasAMappedEndpoint()
    {
        var protoServices = typeof(Design).Assembly.GetTypes()
            .Select(t => t.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static))
            .Where(p => p is not null && p.PropertyType == typeof(ProtoServiceDescriptor))
            .Select(p => ((ProtoServiceDescriptor)p!.GetValue(null)!).FullName)
            .Order()
            .ToArray();
        Assert.Equal(["Cost", "Deploy", "Design"], protoServices);

        using var host = Host("Development");
        var mapped = GrpcEndpoints(host.Host).Select(e => e.Method.ServiceName).ToHashSet();

        Assert.All(protoServices, name => Assert.Contains(name, mapped));
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task AnUnauthenticatedCallToEveryProtectedMethodIsRefused(string environment)
    {
        using var host = Host(environment);
        using var channel = host.Host.CreateGrpcChannel();
        var invoker = channel.CreateCallInvoker();

        var callable = GrpcEndpoints(host.Host)
            .Where(e => !e.IsAnonymous
                        && e.Method.Type is MethodType.Unary or MethodType.ServerStreaming)
            .ToArray();
        // Design (4) + Deploy (16) methods are unary or server-streaming.
        Assert.True(callable.Length >= 20, $"expected at least 20 protected callable methods, got {callable.Length}");

        foreach (var endpoint in callable)
        {
            // The challenge answers before any message is read, so an empty payload is enough.
            var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                if (endpoint.Method.Type == MethodType.Unary)
                {
                    await invoker.AsyncUnaryCall(endpoint.Method, null, new CallOptions(), []).ResponseAsync;
                }
                else
                {
                    using var call = invoker.AsyncServerStreamingCall(endpoint.Method, null, new CallOptions(), []);
                    await call.ResponseStream.MoveNext(CancellationToken.None);
                }
            });
            Assert.True(ex.StatusCode == StatusCode.Unauthenticated,
                $"{endpoint.Path}: expected Unauthenticated, got {ex.StatusCode}");
        }
    }

    [Fact]
    public async Task ATokenWithoutTheScopeIsPermissionDeniedOnProtectedMethods()
    {
        using var channel = factory.CreateGrpcChannel(Guid.NewGuid(), withScope: false);

        var designEx = await Assert.ThrowsAsync<RpcException>(
            () => new Design.DesignClient(channel).GetSavedAsync(new Empty()).ResponseAsync);
        var deployEx = await Assert.ThrowsAsync<RpcException>(
            () => new Deploy.DeployClient(channel).GetGuardrailPolicyAsync(new Empty()).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, designEx.StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, deployEx.StatusCode);
    }

    // --- the mapping helper on its own ---

    [Fact]
    public void TheMappingHelperProtectsAServiceUnlessAnonymousIsStated()
    {
        // RetailPriceService carries no [Authorize] attribute, so the metadata below can only
        // come from the helper.
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddGrpc();
        var app = builder.Build();

        app.MapAdsGrpcService<RetailPriceService>();
        var protectedEndpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints)
            .Where(e => e.Metadata.GetMetadata<GrpcMethodMetadata>() is not null).ToArray();

        Assert.NotEmpty(protectedEndpoints);
        Assert.All(protectedEndpoints, e =>
        {
            Assert.NotEmpty(e.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Null(e.Metadata.GetMetadata<IAllowAnonymous>());
        });

        var anonymousApp = WebApplication.CreateBuilder();
        anonymousApp.Services.AddGrpc();
        var second = anonymousApp.Build();

        second.MapAdsGrpcService<RetailPriceService>(allowAnonymous: true);
        var anonymousEndpoints = ((IEndpointRouteBuilder)second).DataSources.SelectMany(d => d.Endpoints)
            .Where(e => e.Metadata.GetMetadata<GrpcMethodMetadata>() is not null).ToArray();

        Assert.NotEmpty(anonymousEndpoints);
        Assert.All(anonymousEndpoints, e => Assert.NotNull(e.Metadata.GetMetadata<IAllowAnonymous>()));
    }
}

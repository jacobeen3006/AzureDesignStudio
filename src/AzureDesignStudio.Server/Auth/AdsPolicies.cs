using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Authorization;

namespace AzureDesignStudio.Server.Auth;

/// <summary>
/// Authorization registration and endpoint mapping for the web host. Identity itself lives in
/// the Services project (host-neutral); only what a web server has - the fallback policy and
/// the endpoint metadata - lives here.
/// </summary>
public static class AdsPolicies
{
    /// <summary>
    /// Registers authorization with a fallback policy that requires an authenticated user, so an
    /// endpoint added later without authorization metadata is protected by default. App roles
    /// are enforced inside the service bodies, not by named policies on endpoints.
    /// </summary>
    public static IServiceCollection AddAdsAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());
        return services;
    }

    /// <summary>
    /// Maps one gRPC service (with gRPC-Web) as protected, or as explicitly anonymous when
    /// <paramref name="allowAnonymous"/> is true. The argument is the written form of the
    /// anonymous method list; a service never ends up anonymous by omission.
    /// </summary>
    public static GrpcServiceEndpointConventionBuilder MapAdsGrpcService<TService>(
        this IEndpointRouteBuilder endpoints, bool allowAnonymous = false)
        where TService : class
    {
        var builder = endpoints.MapGrpcService<TService>().EnableGrpcWeb();
        if (allowAnonymous)
            builder.AllowAnonymous();
        else
            builder.RequireAuthorization();
        return builder;
    }
}

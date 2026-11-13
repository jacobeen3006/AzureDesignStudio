using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Services;
using AzureDesignStudio.Services.Models;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web.Resource;

namespace AzureDesignStudio.Server.Services
{
    // The services live in AzureDesignStudio.Services, with no web host in them (the desktop
    // app runs the same classes in-process). What only a web server has stays here: the
    // authorization attributes, and the bridge from the authenticated HttpContext user to the
    // caller the services read.

    [Authorize]
    [RequiredScope(RequiredScopesConfigurationKey = "AzureAd:Scopes")]
    public sealed class HostedDesignService(DesignDbContext context, ILogger<DesignService> logger)
        : DesignService(context, logger);

    [Authorize]
    [RequiredScope(RequiredScopesConfigurationKey = "AzureAd:Scopes")]
    public sealed class HostedDeployService(DesignDbContext context, ILogger<DeployService> logger, ICryptoService cryptoService,
        WhatIfTokenGate whatIfGate, IHttpClientFactory httpClientFactory)
        : DeployService(context, logger, cryptoService, whatIfGate, httpClientFactory);

    /// <summary>Puts the request's authenticated user where the services look for the caller
    /// (<see cref="ServiceTools.UserStateKey"/>).</summary>
    public sealed class CallerInterceptor : Interceptor
    {
        public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation)
        {
            SetCaller(context);
            return continuation(request, context);
        }

        public override Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
        {
            SetCaller(context);
            return continuation(request, responseStream, context);
        }

        private static void SetCaller(ServerCallContext context) =>
            context.UserState[ServiceTools.UserStateKey] = context.GetHttpContext().User;
    }
}

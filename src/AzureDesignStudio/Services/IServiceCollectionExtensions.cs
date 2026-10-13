using Bicep.Core.Registry;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Immutable;

namespace AzureDesignStudio.Services;

public sealed class EmptyArtifactRegistryProvider : IArtifactRegistryProvider
{
    public ImmutableArray<string> SupportedSchemes => [];

    public IArtifactRegistry? TryGetRegistry(string scheme) => null;
}

public static class IServiceCollectionExtensions
{
    public static IServiceCollection AddAdsBicepDecompiler(this IServiceCollection services) => services
        .AddBicepDecompiler()
        .AddSingleton<IArtifactRegistryProvider, EmptyArtifactRegistryProvider>()
        .AddSingleton<IAdsBicepDecompiler, AdsBicepDecompiler>();
}

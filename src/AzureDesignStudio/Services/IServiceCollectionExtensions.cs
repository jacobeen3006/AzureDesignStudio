using Bicep.Core.Registry;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Immutable;

namespace AzureDesignStudio.Services;

public sealed class EmptyArtifactRegistryProvider : IArtifactRegistryProvider
{
    public ImmutableArray<IArtifactRegistry> Registries(Uri templateUri) => [];
}

public static class IServiceCollectionExtensions
{
    public static IServiceCollection AddAdsBicepDecompiler(this IServiceCollection services) => services
        .AddSingleton<IArtifactRegistryProvider, EmptyArtifactRegistryProvider>()
        .AddSingleton<IAdsBicepDecompiler, AdsBicepDecompiler>();
}

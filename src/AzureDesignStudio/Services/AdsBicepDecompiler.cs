using Bicep.Decompiler;
using System.Collections.Immutable;

namespace AzureDesignStudio.Services;

public interface IAdsBicepDecompiler
{
    Task<DecompileResult> Decompile(string jsonContent);
}

public record DecompileResult(string? BicepFile, string? Error);

public class AdsBicepDecompiler : IAdsBicepDecompiler
{
    private readonly BicepDecompiler _decompiler;

    public AdsBicepDecompiler(BicepDecompiler decompiler)
    {
        _decompiler = decompiler;
    }

    public async Task<DecompileResult> Decompile(string jsonContent)
    {
        try
        {
            var bicepUri = new Uri("file:///main.bicep");
            var result = await _decompiler.Decompile(bicepUri, jsonContent);

            return new DecompileResult(result.FilesToSave[result.EntrypointUri], null);
        }
        catch (Exception exception)
        {
            return new DecompileResult(null, exception.Message);
        }
    }
}

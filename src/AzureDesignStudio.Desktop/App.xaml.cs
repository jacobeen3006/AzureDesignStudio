using AzureDesignStudio.Models;
using AzureDesignStudio.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzureDesignStudio.Desktop;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Stencils ship inside the app package; load them before the first render,
        // mirroring the WASM host which awaits AdsContext.InitializeAsync before RunAsync.
        var adsContext = MauiProgram.Services.GetRequiredService<AdsContext>();
        adsContext.StencilLoader = () => Task.FromResult<IReadOnlyList<StencilModel>>(
            System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<StencilModel>>(
                System.IO.File.ReadAllText(AppContent.StencilsFile),
                // ads-stencils.json is camelCase; match GetFromJsonAsync's web defaults used by WASM.
                System.Text.Json.JsonSerializerOptions.Web)!);
        try
        {
            adsContext.InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            // The app still opens, with an empty palette - but say why, instead of nothing.
            adsContext.AllStencils = Array.Empty<StencilModel>();
            MauiProgram.Services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?
                .CreateLogger<App>()
                .LogError(e, "The resource palette could not be loaded from ads-stencils.json; the palette is empty.");
        }

        return new Window(new MainPage()) { Title = "Azure Design Studio" };
    }
}

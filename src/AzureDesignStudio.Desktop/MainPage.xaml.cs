namespace AzureDesignStudio.Desktop;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();
		// Telemetry stays off on desktop until AI wiring lands (plan 4.2).
		blazorWebView.RootComponents[0].Parameters = new Dictionary<string, object> { ["EnableTelemetry"] = false };
	}
}

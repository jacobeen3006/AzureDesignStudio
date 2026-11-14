using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>
/// The real server host with only the authentication scheme replaced. It boots on the checked-in
/// sample configuration: no Key Vault name, no Azure credential, no developer database.
/// </summary>
public sealed class AdsServerFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The factory default is Development, which switches on development-only wiring.
        builder.UseEnvironment("Testing");

        // UseSetting, not ConfigureAppConfiguration: top-level Program.cs code cannot see the latter.
        builder.UseSetting("Database:Provider", "InMemory");
        builder.UseSetting("ApplicationInsights:ConnectionString", "");

        builder.ConfigureTestServices(services =>
            services.AddAuthentication(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
    }
}

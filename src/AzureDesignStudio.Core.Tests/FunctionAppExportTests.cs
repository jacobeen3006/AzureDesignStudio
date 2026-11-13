using System.Linq;
using System.Text.RegularExpressions;
using AzureDesignStudio.AzureResources.Storage;
using AzureDesignStudio.AzureResources.Web;
using AzureDesignStudio.Core.Web;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>Offline checks on what a Function App turns into in the exported template.</summary>
    public class FunctionAppExportTests
    {
        private static (AppServicePlanModel Plan, FunctionAppModel App) FunctionApp(string os, string name = "funcapp3425")
        {
            var plan = new AppServicePlanModel { Name = "plan", Kind = os };
            var app = new FunctionAppModel { Name = name, RuntimeStack = "node|~14", HostingPlan = "premium" };
            plan.AddChild(app);
            return (plan, app);
        }

        // The site resource kept the "app" kind it was constructed with: every Function App
        // was exported as a plain Web App.
        [Theory]
        [InlineData("windows", "functionapp")]
        [InlineData("linux", "functionapp,linux")]
        public void FunctionAppIsExportedWithItsOwnKind(string os, string expectedKind)
        {
            var (_, app) = FunctionApp(os);

            var site = Assert.Single(app.GetArmResources().OfType<Sites>());

            Assert.Equal(expectedKind, site.Kind);
        }

        [Fact]
        public void WebAppStaysKindApp()
        {
            var plan = new AppServicePlanModel { Name = "plan", Kind = "linux" };
            var web = new WebAppModel { Name = "web", RuntimeStack = "node|~14" };
            plan.AddChild(web);

            Assert.Equal("app", Assert.Single(web.GetArmResources().OfType<Sites>()).Kind);
        }

        // Container publishing has no runtime stack; reading it used to throw on export.
        [Theory]
        [InlineData("linux")]
        [InlineData("windows")]
        public void ContainerPublishedFunctionAppExportsWithoutARuntimeStack(string os)
        {
            var (_, app) = FunctionApp(os);
            app.Publish = "docker";
            app.RuntimeStack = null!;

            Assert.True(app.IsValid);
            Assert.Single(app.GetArmResources().OfType<Sites>());
        }

        [Theory]
        [InlineData("funcapp3425", "astgfuncapp3425")] // already valid: unchanged
        [InlineData("MyFunc", "astgmyfunc")]
        public void StorageNameForAnAlreadyValidAppNameIsUnchanged(string appName, string expected)
        {
            Assert.Equal(expected, FunctionAppModel.StorageAccountNameFor(appName));
        }

        [Theory]
        [InlineData("orders-func")]
        [InlineData("orders_func.v2")]
        [InlineData("a-function-app-with-a-very-long-name-indeed")]
        [InlineData("İstanbul-fn")]
        [InlineData("---")]
        [InlineData("")]
        public void StorageNameIsAlwaysAValidStorageAccountName(string appName)
        {
            var name = FunctionAppModel.StorageAccountNameFor(appName);

            Assert.Matches(new Regex("^[a-z0-9]{3,24}$"), name);
        }

        [Fact]
        public void NamesThatCleanToTheSameTextStillGetDifferentAccounts()
        {
            Assert.NotEqual(
                FunctionAppModel.StorageAccountNameFor("orders-func"),
                FunctionAppModel.StorageAccountNameFor("orders_func"));
        }

        [Fact]
        public void ExportedStorageAccountCarriesTheSanitisedName()
        {
            var (_, app) = FunctionApp("linux", "orders-func");

            var account = Assert.Single(app.GetArmResources().OfType<StorageAccounts>());

            Assert.Matches(new Regex("^[a-z0-9]{3,24}$"), account.Name);
        }
    }
}

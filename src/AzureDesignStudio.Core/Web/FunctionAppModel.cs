using AutoMapper;
using AzureDesignStudio.AzureResources.Base;
using AzureDesignStudio.AzureResources.Web;
using AzureDesignStudio.Core.Attributes;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Storage;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace AzureDesignStudio.Core.Web
{
    [MapToDto(TypeKey = AdsConstants.FunctionApp, DtoBase = "WebAppModelDto")]
    public class FunctionAppModel : WebAppModel
    {
        public override string ServiceName => "Function App";
        public override Type? DataFormType => typeof(FunctionAppForm);
        [Required, DisplayName("Hosting plan"), MapToDto]
        public string HostingPlan { get; set; } = null!;
        public StorageAccountModel StorageAccount = new()
        {
            Kind = "StorageV2",
            SkuName = "Standard_LRS"
        };
        public override string Kind
        {
            get => ServicePlanOS == "linux" ? "functionapp,linux" : "functionapp";
        }
        public override bool IsValid
        {
            get => !string.IsNullOrEmpty(HostingPlan)
                && (HostingPlan != "consumption" || Publish != "docker")
                && base.IsValid;
        }
        public override AzureNodeDto GetNodeDto(IMapper mapper)
        {
            return mapper.Map<FunctionAppModelDto>(this);
        }
        private void CreateFuncAppSettings(ref SiteConfig siteConfig)
        {
            if (siteConfig.AppSettings == null)
                siteConfig.AppSettings = new List<NameValuePair>();

            // A container-published app has no runtime stack (the form hides the picker), so
            // RuntimeStack is null here; it is only read on the code path.
            var stacks = Stacks();
            if (Publish != "code" || (stacks[0] == "dotnet" && stacks[1] == "v3.1"))
            {
                siteConfig.AppSettings.Add(new()
                {
                    Name = "FUNCTIONS_EXTENSION_VERSION",
                    Value = "~3"
                });
            }
            else
            {
                siteConfig.AppSettings.Add(new()
                {
                    Name = "FUNCTIONS_EXTENSION_VERSION",
                    Value = "~4"
                });
            }

            StorageAccount.Name = StorageAccountNameFor(Name);
            siteConfig.AppSettings.Add(new()
            {
                Name = "AzureWebJobsStorage",
                Value = $"[concat('DefaultEndpointsProtocol=https;AccountName={StorageAccount.Name};AccountKey=',{StorageAccount.AccountKey})]"
            });

            if (Publish == "code")
            {
                siteConfig.AppSettings.Add(new()
                {
                    Name = "FUNCTIONS_WORKER_RUNTIME",
                    Value = stacks[0]
                });
                if (stacks[0].Equals("node", StringComparison.InvariantCultureIgnoreCase))
                {
                    siteConfig.AppSettings.Add(new()
                    {
                        Name = "WEBSITE_NODE_DEFAULT_VERSION",
                        Value = stacks[1]
                    });
                }
            }
            if ((ServicePlanOS == "windows" && HostingPlan == "consumption") || HostingPlan == "premium")
            {
                siteConfig.AppSettings.Add(new()
                {
                    Name = "WEBSITE_CONTENTAZUREFILECONNECTIONSTRING",
                    Value = $"[concat('DefaultEndpointsProtocol=https;AccountName={StorageAccount.Name};AccountKey=', {StorageAccount.AccountKey})]"
                });
            }
        }
        /// <summary>Runtime name and version from "name|version"; always two entries.</summary>
        private string[] Stacks()
        {
            var parts = (RuntimeStack ?? string.Empty).Split('|');
            return [parts[0], parts.Length > 1 ? parts[1] : string.Empty];
        }

        /// <summary>
        /// Name of the backing storage account: 3-24 characters, lower-case letters and digits
        /// only. "astg" + the app name broke that for any name with a hyphen ("orders-func") or
        /// longer than 20 characters. A name that already fits is used as it always was; one
        /// that has to be cleaned or cut gets a short hash of the original so two apps whose
        /// names clean to the same text do not share an account.
        /// </summary>
        public static string StorageAccountNameFor(string appName)
        {
            const string prefix = "astg";
            const int maxLength = 24;
            appName ??= string.Empty;

            var cleaned = new string(appName.ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')).ToArray());
            if (cleaned.Length == appName.Length && prefix.Length + cleaned.Length <= maxLength)
                return prefix + cleaned;

            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(appName)))[..6].ToLowerInvariant();
            var room = maxLength - prefix.Length - hash.Length;
            return prefix + cleaned[..Math.Min(cleaned.Length, room)] + hash;
        }

        protected override void CreateSiteConfigForLinuxCode(ref SiteConfig siteConfig)
        {
            if (Publish == "code")
            {
                var stacks = Stacks();

                siteConfig.LinuxFxVersion = stacks[0] switch
                {
                    "dotnet" => stacks[1] switch
                    {
                        "v6.0" => "DOTNET|6.0",
                        "v3.1" => "DOTNET|3.1",
                        _ => RuntimeStack,
                    },
                    "java" => stacks[1] switch
                    {
                        "11" => "JAVA|11",
                        "1.8" => "JAVA|8",
                        _ => RuntimeStack,
                    },
                    "node" => stacks[1] switch
                    {
                        "~14" => "NODE|14",
                        "~12" => "NODE|12",
                        _ => RuntimeStack,
                    },
                    _ => RuntimeStack,
                };
            }
            else
            {
                siteConfig.LinuxFxVersion = "DOCKER|mcr.microsoft.com/azure-functions/dotnet:3.0-appservice-quickstart";
                siteConfig.AppSettings ??= new List<NameValuePair>();

                siteConfig.AppSettings.Add(new()
                {
                    Name = "DOCKER_REGISTRY_SERVER_URL",
                    Value = "https://mcr.microsoft.com"
                });
                siteConfig.AppSettings.Add(new()
                {
                    Name = "DOCKER_REGISTRY_SERVER_USERNAME",
                    Value = string.Empty
                });
                siteConfig.AppSettings.Add(new()
                {
                    Name = "DOCKER_REGISTRY_SERVER_PASSWORD",
                    Value = string.Empty
                });
                siteConfig.AppSettings.Add(new()
                {
                    Name = "WEBSITES_ENABLE_APP_SERVICE_STORAGE",
                    Value = "false"
                });
            }
        }
        protected override void CreateSiteConfigForWinCode(ref SiteConfig siteConfig)
        {
            if (Publish != "code")
                return;

            var stacks = Stacks();

            switch (stacks[0])
            {
                case "dotnet":
                    siteConfig.NetFrameworkVersion = stacks[1];
                    break;
                case "java":
                    siteConfig.JavaVersion = stacks[1];
                    break;
                case "PowerShell":
                    siteConfig.PowerShellVersion = "~7";
                    break;
            }
        }
        protected override SiteConfig CreateSiteConfig()
        {
            SiteConfig siteConfig = new()
            {
                Cors = new()
                {
                    AllowedOrigins = new List<string> { "https://portal.azure.com" },
                }
            };

            CreateFuncAppSettings(ref siteConfig);

            if (ServicePlanOS == "linux")
            {
                CreateSiteConfigForLinuxCode(ref siteConfig);
            }
            else
            {
                CreateSiteConfigForWinCode(ref siteConfig);
            }

            return siteConfig;
        }

        public override AppServicePlanModel CreateHostingPlan()
        {
            if (Group is not AppServicePlanModel servicePlan)
                throw new Exception($"{ServiceName} must be in a service plan");

            if (HostingPlan == "consumption" && Publish == "docker")
            {
                throw new Exception($"{ServiceName} doesn't support using docker container on Consumption plan.");
            }

            switch (HostingPlan)
            {
                case "consumption":
                    {
                        servicePlan.Kind = ServicePlanOS;
                        servicePlan.SkuName = "Y1";
                        servicePlan.SkuTier = "Dynamic";
                        break;
                    }
                case "premium":
                    {
                        servicePlan.Kind = ServicePlanOS;
                        // An Elastic Premium size already on the plan (EP2, EP3) is kept.
                        if (servicePlan.SkuTier != "ElasticPremium")
                        {
                            servicePlan.SkuName = "EP1";
                            servicePlan.SkuTier = "ElasticPremium";
                        }
                        break;
                    }
                case "asp":
                    {
                        servicePlan.Kind = ServicePlanOS;
                        // Dedicated hosting runs on whatever size the plan has. Every export
                        // used to force P1V2 here, overwriting the plan's own SKU - and the
                        // SKU of every web app sharing the plan. Only a serverless SKU left
                        // by an earlier hosting choice is replaced.
                        if (servicePlan.IsServerless)
                            servicePlan.UseDedicatedSku(AppServicePlanModel.DefaultDedicatedSku);
                        break;
                    }
                default:
                    base.CreateHostingPlan();
                    break;
            }
            ;

            return servicePlan;
        }

        public override IList<ResourceBase> GetArmResources()
        {
            var result = base.GetArmResources().ToList();

            ArmResource.DependsOn.Add(StorageAccount.ResourceId);

            result.AddRange(StorageAccount.GetArmResources());

            return result;
        }
    }
}

using AzureDesignStudio.Core.Cache;
using AzureDesignStudio.Core.Compute;
using AzureDesignStudio.Core.APIM;
using AzureDesignStudio.Core.ContainerRegistry;
using AzureDesignStudio.Core.KeyVault;
using AzureDesignStudio.Core.Messaging;
using AzureDesignStudio.Core.Models;
using AzureDesignStudio.Core.Network;
using AzureDesignStudio.Core.SQL;
using AzureDesignStudio.Core.Storage;
using AzureDesignStudio.Core.Web;
using Blazor.Diagrams.Core.Models;

namespace AzureDesignStudio.Core.Cost
{
    /// <summary>Extracts the priced SKU string from a diagram node model, or null when the node carries none.</summary>
    public static class CostSkuResolver
    {
        public static string? Resolve(NodeModel node) => node switch
        {
            VirtualMachineModel => null,                       // form exposes no size; cost map default applies
            APIMModel apim => apim.SkuName,
            AppServicePlanModel plan => plan.SkuName,
            SqlDatabaseModel db => db.SkuName,
            AzureRedisModel redis => redis.Sku,
            ContainerRegistryModel registry => registry.Sku,
            ServiceBusModel bus => bus.Sku,
            EventHubsModel hubs => hubs.Sku,
            KeyVaultModel vault => vault.Sku,
            StorageAccountModel storage => storage.SkuName,
            AzureFirewallModel firewall => firewall.Sku,
            AppGatewayModel gw => gw.Tier,
            LoadBalancerModel lb => lb.Sku,
            PublicIpModel ip => ip.Sku,
            IAzureNode => null,                                // priced via default SKU or usage-only
            _ => null,
        };
    }
}

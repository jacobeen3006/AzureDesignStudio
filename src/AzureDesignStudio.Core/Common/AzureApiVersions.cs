using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AzureDesignStudio.AzureResources.Base;

namespace AzureDesignStudio.Core.Common
{
    /// <summary>
    /// ARM resource type -> api-version read from the generated AzureResources models'
    /// [AzureResource] attributes - the repo's single source of truth, no hand-maintained table.
    /// Where the models carry both a stable and a preview version for one type, the highest
    /// stable wins; a preview is kept only when nothing stable exists (e.g. ApiManagement).
    /// Case-insensitive lookup, built once, thread-safe.
    /// </summary>
    public static class AzureApiVersions
    {
        private static readonly Lazy<Dictionary<string, string>> MapLazy = new(Build, isThreadSafe: true);

        /// <summary>Case-insensitive live view of the attribute table.</summary>
        public static IReadOnlyDictionary<string, string> Map => MapLazy.Value;

        private static Dictionary<string, string> Build()
        {
            var attributed = typeof(AzureResourceAttribute).Assembly
                .GetTypes()
                .SelectMany(t => t.GetCustomAttributes<AzureResourceAttribute>(inherit: false))
                .Where(a => !string.IsNullOrWhiteSpace(a.ResourceType) && !string.IsNullOrWhiteSpace(a.ApiVersion));

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in attributed.GroupBy(a => a.ResourceType, StringComparer.OrdinalIgnoreCase))
            {
                var versions = group.Select(a => a.ApiVersion).ToList();
                var stable = versions.Where(v => !v.Contains("preview", StringComparison.OrdinalIgnoreCase)).ToList();
                map[group.Key] = (stable.Count > 0 ? stable : versions).Max(StringComparer.Ordinal)!;
            }
            return map;
        }
    }
}

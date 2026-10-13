// Licensed under the MIT License. See LICENSE in the project root for license information.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureDesignStudio.Core.Validation
{
    public enum ValidationSeverity
    {
        Info,
        Warning,
        Error,
    }

    public record ValidationIssue(ValidationSeverity Severity, string RuleId, string NodeName, string Message);

    public static class ValidationJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };
    }
}

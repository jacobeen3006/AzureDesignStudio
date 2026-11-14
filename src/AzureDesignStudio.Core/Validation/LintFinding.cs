// Licensed under the MIT License. See LICENSE in the project root for license information.
namespace AzureDesignStudio.Core.Validation
{
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public enum LintPillar
    {
        Reliability,
        Security,
        CostOptimization,
        OperationalExcellence,
        PerformanceEfficiency,
    }

    /// <summary>One entry of the linter's rule catalog.</summary>
    public sealed record LintRule(string Id, LintPillar Pillar, ValidationSeverity Severity, string Title, string LearnUrl);

    /// <summary>Well-Architected advice about a design. Never blocks a deployment.</summary>
    public sealed record LintFinding(string RuleId, LintPillar Pillar, ValidationSeverity Severity,
        string NodeName, string Message, string LearnUrl)
    {
        /// <summary>True for an absolute https URL: the only kind the UI renders as a link.</summary>
        public static bool HasSafeLearnUrl(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    }
}

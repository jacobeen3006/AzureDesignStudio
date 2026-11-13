// Licensed under the MIT License. See LICENSE in the project root for license information.
namespace AzureDesignStudio.Core.Validation
{
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
        string NodeName, string Message, string LearnUrl);
}

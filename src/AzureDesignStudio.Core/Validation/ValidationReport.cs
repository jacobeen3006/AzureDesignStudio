// Licensed under the MIT License. See LICENSE in the project root for license information.
using System.Text.Json;
using Blazor.Diagrams.Core.Models;

namespace AzureDesignStudio.Core.Validation
{
    /// <summary>What the validation drawer shows: blocking issues plus Well-Architected advice.</summary>
    public sealed record ValidationReport(IReadOnlyList<ValidationIssue> Issues, IReadOnlyList<LintFinding> Findings)
    {
        public static ValidationReport For(IEnumerable<NodeModel> nodes)
        {
            var list = nodes as IReadOnlyCollection<NodeModel> ?? nodes.ToList();
            return new ValidationReport(DesignValidator.Validate(list), DesignLinter.Lint(list));
        }

        /// <summary>Reads a serialized report; a bare array of issues (the old payload) parses as issues without findings.</summary>
        public static ValidationReport Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new ValidationReport(Array.Empty<ValidationIssue>(), Array.Empty<LintFinding>());

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return new ValidationReport(
                    JsonSerializer.Deserialize<List<ValidationIssue>>(json, ValidationJson.Options) ?? new(),
                    Array.Empty<LintFinding>());

            var report = JsonSerializer.Deserialize<ValidationReport>(json, ValidationJson.Options);
            return new ValidationReport(report?.Issues ?? Array.Empty<ValidationIssue>(), report?.Findings ?? Array.Empty<LintFinding>());
        }
    }
}

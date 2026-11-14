using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Keeps <c>docs/identity-and-guardrails.md</c> (the operator guide for roles, tenant scope, response codes
    /// and the administrator call recipe for the tenant settings) and the appRoles section of
    /// <c>docs/entra-setup.md</c> honest. They fail when the guide loses a statement an operator depends on,
    /// cites a repository path that moved, or grows a credential or a token, and when AGENTS.md stops pointing
    /// at the guide. They only read text files.
    /// </summary>
    public class IdentityRunbookTests
    {
        private const string GuideRelativePath = "docs/identity-and-guardrails.md";

        // Every statement an operator relies on. Role names, setting names, response codes, the crypto switch,
        // the personal-account tenant, the upgrade consequence and the call recipe markers.
        private static readonly string[] RequiredStatements =
        [
            "Guardrail.Admin",
            "Deploy.Approver",
            "allow_self_approval",
            "guardrail_policy_mode",
            "INTERPRETATION TO CONFIRM",
            "IDENTITY_INVALID",
            "GUARDRAIL_ADMIN_REQUIRED",
            "TENANT_SETTINGS_ADMIN_REQUIRED",
            "TENANT_SETTINGS_INVALID",
            "PLAN_APPROVER_ROLE_REQUIRED",
            "PLAN_SELF_APPROVAL_REFUSED",
            "PLAN_REQUESTER_MISMATCH",
            "PLAN_APPROVER_MISMATCH",
            "DECOM_APPROVER_ROLE_REQUIRED",
            "Crypto__Provider",
            "9188040d-6c67-4c5b-b112-36a304b66dad",
            "re-approved",
            "Deploy/GetTenantSettings",
            "Deploy/SetTenantSettings",
            "grpcurl",
            "-import-path",
            "GetTenantSettingsAsync",
            "SetTenantSettingsAsync",
            "Changing the tenant settings (administrators)",
            "Cost.GetRetailPrices",
        ];

        [Fact]
        public void Guide_ContainsEveryRequiredStatement()
        {
            var text = ReadGuide();
            var missing = RequiredStatements.Where(s => !text.Contains(s, StringComparison.Ordinal)).ToList();

            Assert.True(missing.Count == 0,
                $"{GuideRelativePath} lost required statements: {string.Join(" | ", missing)}");
        }

        [Fact]
        public void Guide_RecordsTheInterpretationExactlyOnce()
        {
            var count = Regex.Matches(ReadGuide(), "INTERPRETATION TO CONFIRM", RegexOptions.None).Count;

            Assert.True(count == 1,
                $"{GuideRelativePath} must carry the policy-mode interpretation notice once (heading), found {count}.");
        }

        [Fact]
        public void Guide_PassesTheTokenThroughAnEnvironmentVariable()
        {
            var text = ReadGuide();

            Assert.Contains("ADS_TOKEN", text, StringComparison.Ordinal);
            Assert.Contains("Authorization: Bearer $ADS_TOKEN", text, StringComparison.Ordinal);
            Assert.Contains("Environment.GetEnvironmentVariable(\"ADS_TOKEN\")", text, StringComparison.Ordinal);

            // Any other text after "Bearer " must be a variable or a format placeholder, never a literal value.
            var literal = Regex.Matches(text, @"Bearer\s+(?![$\{<])[A-Za-z0-9._~+/=-]{12,}")
                .Select(m => m.Value).ToList();
            Assert.True(literal.Count == 0,
                $"{GuideRelativePath} must not show a bearer token value: {string.Join(", ", literal)}");
        }

        [Fact]
        public void Guide_CitesOnlyRepositoryPathsThatExist()
        {
            var root = RepositoryRoot();
            var text = ReadGuide();
            var missing = new List<string>();

            // Rule 1: a backtick-quoted token that starts with src/, has no placeholder, glob or space.
            foreach (Match span in Regex.Matches(StripFences(text), "`([^`\\r\\n]+)`"))
            {
                var token = span.Groups[1].Value;
                if (!token.StartsWith("src/", StringComparison.Ordinal)) continue;
                if (token.IndexOfAny(['<', '>', '*', ' ']) >= 0) continue;
                if (!PathExists(root, token)) missing.Add(token);
            }

            // Rule 2: the same check for src/ paths anywhere, so a moved project in a command line fails too.
            foreach (Match path in Regex.Matches(text, @"(?<![\w./-])src/[A-Za-z0-9_./-]*[A-Za-z0-9_]"))
                if (!PathExists(root, path.Value)) missing.Add(path.Value);

            Assert.True(missing.Count == 0,
                $"{GuideRelativePath} cites repository paths that do not exist: {string.Join(", ", missing.Distinct())}");
        }

        [Fact]
        public void Guide_ContainsNoCredentialAndNoJwtShapedText()
        {
            var text = ReadGuide();
            var found = new[] { "Password=", "User ID=", "Server=", "AccountKey=", "SharedAccessKey=", "-----BEGIN" }
                .Where(p => text.Contains(p, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(found.Count == 0,
                $"{GuideRelativePath} must use placeholders only; it contains: {string.Join(", ", found)}");

            // A JWT starts with a base64url-encoded JSON header: eyJ...
            Assert.False(Regex.IsMatch(text, @"eyJ[A-Za-z0-9_-]{10,}"),
                $"{GuideRelativePath} contains JWT-shaped text.");
        }

        [Fact]
        public void EntraGuide_DefinesTheAppRoles()
        {
            var path = Path.Combine(RepositoryRoot(), "docs", "entra-setup.md");
            Assert.True(File.Exists(path), "docs/entra-setup.md does not exist.");
            var text = File.ReadAllText(path);

            Assert.Contains("appRoles", text, StringComparison.Ordinal);
            Assert.Contains("\"value\": \"Guardrail.Admin\"", text, StringComparison.Ordinal);
            Assert.Contains("\"value\": \"Deploy.Approver\"", text, StringComparison.Ordinal);
            Assert.Contains("docs/identity-and-guardrails.md", text, StringComparison.Ordinal);
        }

        [Fact]
        public void AgentsMd_PointsAtTheGuide()
        {
            var agents = File.ReadAllText(Path.Combine(RepositoryRoot(), "AGENTS.md"));

            Assert.Contains("docs/identity-and-guardrails.md", agents, StringComparison.Ordinal);
        }

        [Fact]
        public void AgentsMd_StatesTheIdentityConventions()
        {
            var agents = File.ReadAllText(Path.Combine(RepositoryRoot(), "AGENTS.md"));
            string[] required =
            [
                "Guardrail.Admin",
                "Deploy.Approver",
                "ServiceTools.TryGetCaller",
                "SingleUserHostKey",
                "Cost.GetRetailPrices",
                "Crypto:Provider",
                "TenantApprovals",
                "INTERPRETATION TO CONFIRM",
                "service-worker.published.js",
            ];
            var missing = required.Where(r => !agents.Contains(r, StringComparison.Ordinal)).ToList();

            Assert.True(missing.Count == 0, $"AGENTS.md lost identity conventions: {string.Join(" | ", missing)}");
        }

        private static string ReadGuide()
        {
            var path = Path.Combine(RepositoryRoot(), "docs", "identity-and-guardrails.md");
            Assert.True(File.Exists(path), $"{GuideRelativePath} does not exist.");
            return File.ReadAllText(path);
        }

        private static bool PathExists(string root, string relative)
        {
            var full = Path.Combine(root, relative.TrimEnd('/'));
            return File.Exists(full) || Directory.Exists(full);
        }

        private static string StripFences(string text) =>
            Regex.Replace(text, "```.*?```", string.Empty, RegexOptions.Singleline);

        /// <summary>Walks up from the test output directory to the directory holding global.json.</summary>
        private static string RepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "global.json")))
                    return dir.FullName;
                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                $"Repository root not found: no global.json in '{AppContext.BaseDirectory}' or any parent directory.");
        }
    }
}

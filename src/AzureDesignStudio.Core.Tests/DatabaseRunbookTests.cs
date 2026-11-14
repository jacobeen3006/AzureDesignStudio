using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Keeps <c>docs/database-operations.md</c> honest. The runbook is the only place an operator learns how to
    /// recover a stuck migration lock, restore a backup or roll back SQL Server, so these tests fail when it loses a
    /// recovery statement, cites a repository path that moved, or grows a credential, and when AGENTS.md stops
    /// pointing at it. They only read text files.
    /// </summary>
    public class DatabaseRunbookTests
    {
        private const string RunbookRelativePath = "docs/database-operations.md";

        // Workspace and deploy.db half of the runbook.
        private static readonly string[] WorkspaceStatements =
        [
            "DELETE FROM \"__EFMigrationsLock\"",
            "ADS_DATA_DIR",
            "workspace.corrupt-",
            "workspace.pre-restore-",
            "restore-staging",
            "PRAGMA integrity_check",
            "upgrade-removed-rows",
            "ReadWriteQueryOnly",
            "Immutable",
            "--self-check",
            "backups/deploy",
            "plaintext",
        ];

        [Fact]
        public void Runbook_ExplainsTheWorkspaceRecoveries()
        {
            var text = ReadRunbook();
            var missing = WorkspaceStatements.Where(s => !text.Contains(s, StringComparison.Ordinal)).ToList();

            Assert.True(missing.Count == 0,
                $"{RunbookRelativePath} lost required recovery statements: {string.Join(" | ", missing)}");
        }

        [Fact]
        public void Runbook_CitesOnlyRepositoryPathsThatExist()
        {
            var root = RepositoryRoot();
            var text = ReadRunbook();
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
                $"{RunbookRelativePath} cites repository paths that do not exist: {string.Join(", ", missing.Distinct())}");
        }

        [Fact]
        public void Runbook_QuotesTheLockStatementThatTheExceptionMessagePrints()
        {
            var root = RepositoryRoot();
            var schema = File.ReadAllText(Path.Combine(root, "src", "AzureDesignStudio.Workspace", "WorkspaceSchema.cs"));

            // The exception builds: DELETE FROM "{LockTable}"; with LockTable = "__EFMigrationsLock".
            Assert.Contains("LockTable = \"__EFMigrationsLock\"", schema, StringComparison.Ordinal);
            Assert.Contains("DELETE FROM \\\"{LockTable}\\\";", schema, StringComparison.Ordinal);
            Assert.Contains("DELETE FROM \"__EFMigrationsLock\";", ReadRunbook(), StringComparison.Ordinal);
        }

        [Fact]
        public void Runbook_ContainsNoConnectionStringOrCredential()
        {
            var text = ReadRunbook();
            var found = new[] { "Password=", "User ID=", "Server=", "AccountKey=", "SharedAccessKey=", "-----BEGIN" }
                .Where(p => text.Contains(p, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(found.Count == 0,
                $"{RunbookRelativePath} must use placeholders only; it contains: {string.Join(", ", found)}");
        }

        private static string ReadRunbook()
        {
            var path = Path.Combine(RepositoryRoot(), "docs", "database-operations.md");
            Assert.True(File.Exists(path), $"{RunbookRelativePath} does not exist.");
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

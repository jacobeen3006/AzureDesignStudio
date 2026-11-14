using System.Text.RegularExpressions;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Hosting;

/// <summary>
/// Source-level guards for the structural guarantees of the server foundation. They are properties
/// of source text (no compile-time provider choice, one schema-bootstrap call site, no test
/// authentication in the shipped host), so a behavior test cannot notice them regressing. The tests
/// only read files.
/// </summary>
public class SourceGuardTests
{
    private const string ProgramPath = "src/AzureDesignStudio.Server/Program.cs";
    private const string BootstrapperPath = "src/AzureDesignStudio.Server/Persistence/DatabaseBootstrapper.cs";
    private const string ServerDir = "src/AzureDesignStudio.Server";
    private const string ServicesDir = "src/AzureDesignStudio.Services";
    private const string ServicesCsprojPath = "src/AzureDesignStudio.Services/AzureDesignStudio.Services.csproj";

    private static readonly Regex PreprocessorConditional =
        new(@"^\s*#\s*(if|elif|else|endif)\b", RegexOptions.Compiled);

    private static readonly Regex EnsureCreatedCall =
        new(@"\.\s*EnsureCreated(Async)?\s*\(", RegexOptions.Compiled);

    // DeployDbStartup.Migrate(...) is the Desktop's call into the one schema owner that lives in
    // DeployDbStartup.cs; it is not an EF migration call, so the lookbehind leaves it alone while
    // db.Database.Migrate() anywhere else still matches.
    private static readonly Regex MigrateCall =
        new(@"(?<!DeployDbStartup)\.\s*Migrate(Async)?\s*\(", RegexOptions.Compiled);

    /// <summary>The only files allowed to run schema migrations. The Workspace and Desktop files
    /// arrive in later plans, so the guard checks a subset and never requires them to exist.</summary>
    private static readonly string[] MigrationSites =
    [
        "src/AzureDesignStudio.Server/Persistence/DatabaseBootstrapper.cs",
        "src/AzureDesignStudio.Workspace/WorkspaceSchema.cs",
        "src/AzureDesignStudio.Desktop/DeployDbStartup.cs",
    ];

    [Fact]
    public void ProgramHasNoPreprocessorConditionals()
    {
        var offenders = new List<string>();
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), ProgramPath));
        for (var i = 0; i < lines.Length; i++)
        {
            if (PreprocessorConditional.IsMatch(lines[i]))
                offenders.Add($"{ProgramPath}:{i + 1}: {lines[i].Trim()}");
        }

        Assert.True(offenders.Count == 0,
            "Program.cs must not choose providers or diagnostics at compile time; configuration decides. Found:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void EnsureCreatedIsCalledOnlyByTheDatabaseBootstrapper()
    {
        var root = RepositoryRoot();
        var callers = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var relative in SourceFiles(root, ServerDir).Concat(SourceFiles(root, ServicesDir)))
        {
            var lines = File.ReadAllLines(Path.Combine(root, relative));
            for (var i = 0; i < lines.Length; i++)
            {
                if (EnsureCreatedCall.IsMatch(lines[i]))
                {
                    if (!callers.TryGetValue(relative, out var hits))
                        callers[relative] = hits = new List<int>();
                    hits.Add(i + 1);
                }
            }
        }

        var description = string.Join(Environment.NewLine,
            callers.Select(c => $"{c.Key}: line(s) {string.Join(", ", c.Value)}"));
        Assert.True(
            callers.Count == 1 && callers.ContainsKey(BootstrapperPath),
            $"EnsureCreated may be called only from {BootstrapperPath} (once, at startup), never on a request path. Found in:"
            + Environment.NewLine + (description.Length == 0 ? "(no file)" : description));
    }

    [Fact]
    public void MigrateIsCalledOnlyBySchemaOwners()
    {
        var root = RepositoryRoot();
        var callers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var project in Directory.EnumerateDirectories(Path.Combine(root, "src"))
                     .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                     .Where(relative => !relative.EndsWith(".Tests", StringComparison.Ordinal)))
        {
            // Generated migration classes are skipped: they declare Up/Down and never run a migration.
            foreach (var relative in SourceFiles(root, project).Where(r => !r.Split('/').Contains("Migrations")))
            {
                if (File.ReadAllLines(Path.Combine(root, relative)).Any(line => MigrateCall.IsMatch(line)))
                    callers.Add(relative);
            }
        }

        var offenders = callers.Where(c => !MigrationSites.Contains(c, StringComparer.Ordinal)).ToList();
        Assert.True(offenders.Count == 0,
            "Schema changes run only at the three startup sites (" + string.Join(", ", MigrationSites)
            + "), after the premigration backup; the read-only MCP project must never migrate. Found in:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));

        Assert.DoesNotContain(callers, c => c.StartsWith("src/AzureDesignStudio.Mcp/", StringComparison.Ordinal));
    }

    [Fact]
    public void NoOtherProjectCallsTheModelCreationMethod()
    {
        var root = RepositoryRoot();
        string[] projects =
        [
            "src/AzureDesignStudio.Workspace",
            "src/AzureDesignStudio.Desktop",
            "src/AzureDesignStudio.Mcp",
        ];
        var hits = new List<string>();
        foreach (var relative in projects.SelectMany(project => SourceFiles(root, project))
                     .Where(r => !r.Split('/').Contains("Migrations")))
        {
            var lines = File.ReadAllLines(Path.Combine(root, relative));
            for (var i = 0; i < lines.Length; i++)
            {
                if (EnsureCreatedCall.IsMatch(lines[i]))
                    hits.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(hits.Count == 0,
            "Schema comes from migrations at the three startup sites (" + string.Join(", ", MigrationSites)
            + "); the server bootstrapper's dev/test branch is the only remaining model-creation call. Found in:"
            + Environment.NewLine + string.Join(Environment.NewLine, hits));
    }

    [Fact]
    public void ProductionServerHasNoTestAuthentication()
    {
        var root = RepositoryRoot();
        string[] forbidden = ["TestAuthHandler", "x-test-user", "x-test-no-scope"];
        var offenders = new List<string>();
        foreach (var relative in SourceFiles(root, ServerDir))
        {
            var lines = File.ReadAllLines(Path.Combine(root, relative));
            for (var i = 0; i < lines.Length; i++)
            {
                var hit = forbidden.FirstOrDefault(f => lines[i].Contains(f, StringComparison.Ordinal));
                if (hit is not null)
                    offenders.Add($"{relative}:{i + 1}: {hit}");
            }
        }

        Assert.True(offenders.Count == 0,
            "The header-driven test authentication scheme is a credential bypass and must live only in Server.Tests. Found:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void ServicesProjectStaysFreeOfWebHostAndProviderPackages()
    {
        string[] forbidden =
        [
            "Microsoft.EntityFrameworkCore.SqlServer",
            "Microsoft.EntityFrameworkCore.Sqlite",
            "Microsoft.AspNetCore",
        ];
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), ServicesCsprojPath));
        var found = forbidden.Where(f => text.Contains(f, StringComparison.Ordinal)).ToList();

        Assert.True(found.Count == 0,
            $"{ServicesCsprojPath} runs in-process in the desktop app (no ASP.NET Core on Mac Catalyst) and database providers are the host's choice. Found: "
            + string.Join(", ", found));
    }

    // D-04: the single-user host marker is set by exactly one component (the in-process invoker) and read by
    // exactly one (ServiceTools.TryGetCaller). A claim, a header or a Server file naming the key could forge it.
    private const string SingleUserMarkerName = "SingleUserHostKey";

    private static readonly string[] SingleUserMarkerSites =
    [
        "src/AzureDesignStudio.Services/ServiceTools.cs",
        "src/AzureDesignStudio.Services/InProcess/InProcessCallInvoker.cs",
    ];

    // D-08: the one file that interprets the oid and tid claims.
    private const string IdentityPath = "src/AzureDesignStudio.Services/Auth/AdsIdentity.cs";

    private static readonly Regex ClaimInterpretation = new(
        @"identity/claims/(objectidentifier|tenantid)"
        + @"|\b(ObjectIdUriClaim|ObjectIdShortClaim|TenantIdUriClaim|TenantIdShortClaim)\b"
        + "|Find(First|All)\\s*\\(\\s*\"(oid|tid)\"",
        RegexOptions.Compiled);

    [Fact]
    public void SingleUserMarkerIsReferencedOnlyByTheCallerToolsAndTheInProcessInvoker()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        foreach (var project in Directory.EnumerateDirectories(Path.Combine(root, "src"))
                     .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                     .Where(relative => !relative.EndsWith(".Tests", StringComparison.Ordinal)))
        {
            foreach (var relative in SourceFiles(root, project, "*.cs", "*.razor"))
            {
                if (SingleUserMarkerSites.Contains(relative, StringComparer.Ordinal))
                    continue;
                if (File.ReadAllText(Path.Combine(root, relative)).Contains(SingleUserMarkerName, StringComparison.Ordinal))
                    offenders.Add(relative);
            }
        }

        Assert.True(offenders.Count == 0,
            $"{SingleUserMarkerName} may be referenced only by {string.Join(" and ", SingleUserMarkerSites)}; a host marker that "
            + "anything else can set or read is a forgeable single-user claim (D-04). Found in:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));

        // The allowed sites must actually use it, so the guard cannot go green by the constant being renamed.
        foreach (var site in SingleUserMarkerSites)
            Assert.Contains(SingleUserMarkerName, File.ReadAllText(Path.Combine(root, site)), StringComparison.Ordinal);
    }

    [Fact]
    public void ServerProjectNeverChangesInboundClaimMapping()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        foreach (var relative in SourceFiles(root, ServerDir))
        {
            var lines = File.ReadAllLines(Path.Combine(root, relative));
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("MapInboundClaims", StringComparison.Ordinal))
                    offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "The server keeps the default inbound claim mapping: AdsIdentity accepts both spellings, and changing the "
            + "mapping silently changes which spelling reaches it (D-08). Found:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void OnlyAdsIdentityInterpretsTheObjectIdAndTenantIdClaims()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        foreach (var relative in SourceFiles(root, ServicesDir).Concat(SourceFiles(root, ServerDir))
                     .Where(r => !r.Split('/').Contains("Migrations"))
                     .Where(r => !string.Equals(r, IdentityPath, StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(Path.Combine(root, relative));
            for (var i = 0; i < lines.Length; i++)
            {
                if (ClaimInterpretation.IsMatch(lines[i]))
                    offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            $"Only {IdentityPath} may interpret the oid and tid claims; a second derivation can disagree about who the "
            + "caller or its tenant is (D-08). Found:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));

        Assert.True(ClaimInterpretation.IsMatch(File.ReadAllText(Path.Combine(root, IdentityPath))),
            $"{IdentityPath} no longer matches the claim-interpretation pattern, so this guard has gone blind.");
    }

    /// <summary>Walks up from the test output directory to the directory holding global.json.</summary>
    private static string RepositoryRoot()
    {
        var start = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
                return dir.FullName;
            dir = Directory.GetParent(dir.FullName);
        }

        throw new InvalidOperationException(
            $"Repository root not found: no global.json in '{start}' or any parent directory. The source guards need the checkout.");
    }

    /// <summary>Repository-relative (forward-slash) paths of the .cs files under a source directory, skipping bin and obj.</summary>
    private static IEnumerable<string> SourceFiles(string root, string relativeDir, params string[] patterns)
    {
        var baseDir = Path.Combine(root, relativeDir);
        return (patterns.Length == 0 ? ["*.cs"] : patterns)
            .SelectMany(pattern => Directory.EnumerateFiles(baseDir, pattern, SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(relative => !relative.Split('/').Any(segment => segment is "bin" or "obj"))
            .OrderBy(relative => relative, StringComparer.Ordinal);
    }
}

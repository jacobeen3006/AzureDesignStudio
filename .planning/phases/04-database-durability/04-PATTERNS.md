# Phase 4: Database Durability - Pattern Map

**Mapped:** 2026-11-14
**Files analyzed:** 33 new/modified
**Analogs found:** 26 / 33 (the rest have no in-repo analog; use 04-RESEARCH.md Code Examples)

All analog paths below were checked as git-tracked source (no gitignored mirrors). Paths are relative to the repo root; line numbers are from the files as of commit 769ba91.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `.config/dotnet-tools.json` (new) | config | n/a | none (no tool manifest exists) | no analog |
| `Directory.Packages.props` (mod: add EF Design 10.0.12) | config | n/a | same file, lines 39-43 EF entries | exact |
| `src/AzureDesignStudio.Workspace/AzureDesignStudio.Workspace.csproj` + `packages.lock.json` (mod) | config | n/a | `src/AzureDesignStudio.Server/*.csproj` (`Include=` only) | role-match |
| `src/AzureDesignStudio.Workspace/WorkspaceConnection.cs` (new: options factory, PragmaInterceptor, reader ladder) | utility | request-response | `WorkspaceDbContext.cs` lines 84-100, 187-193 (`WorkspaceDb` ctor/`CreateContext`) + `Mcp/AdsWorkspace.cs` 19-35 | role-match |
| `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs` (mod: drop DDL/EnsureCreated, use factory) | model/service | CRUD | itself | exact |
| `src/AzureDesignStudio.Workspace/Migrations/*Baseline*.cs`, snapshot (new) | migration | batch | `WorkspaceDbContext.cs` 128-182 (the guarded DDL to be moved) | exact (source text) |
| `src/AzureDesignStudio.Workspace/WorkspaceDbContextFactory.cs` (new) | config | n/a | `Server/Persistence/DesignDbContextFactory.cs` | exact |
| `src/AzureDesignStudio.Workspace/WorkspaceStartup.cs` (new state machine) | service | file-I/O + batch | `WorkspaceDb` ctor (97-99) + `LegacyDesignImporter.ImportIfNeeded` | role-match |
| `src/AzureDesignStudio.Workspace/LegacyAdoption.cs` + `UniqueIndexGuard` (new) | utility | batch | `WorkspaceDbContext.cs` `EnsureAddedColumns` 102-118 | exact |
| `src/AzureDesignStudio.Workspace/Integrity/WorkspaceIntegrity.cs` (new) | utility | file-I/O | `Mcp/AdsWorkspace.cs` `HasTableAsync` 38-46 (raw SqliteConnection command) | partial |
| `src/AzureDesignStudio.Workspace/Backup/{WorkspaceBackup,BackupManifest,BackupRetention}.cs` (new) | service/utility | file-I/O, transform | `Workspace/LegacyDesignImporter.cs` (file I/O into the workspace) | partial |
| `src/AzureDesignStudio.Mcp/AdsWorkspace.cs` (mod: use ladder, expose OpenMode) | service | request-response | itself | exact |
| `src/AzureDesignStudio.Mcp/Program.cs` (mod: write OpenMode to stderr) | config | n/a | itself | exact |
| `src/AzureDesignStudio.Services/Models/SqliteDesignDbContext.cs` (new) | model | CRUD | `Services/Models/DesignDbContext.cs` | exact |
| `src/AzureDesignStudio.Services/Migrations/Sqlite/*` (new) | migration | batch | generated; no hand analog | no analog |
| `src/AzureDesignStudio.Server/Persistence/SqlServerDesignDbContext.cs` (new) | model | CRUD | `DesignDbContextFactory.cs` + `DesignDbContext` | role-match |
| `src/AzureDesignStudio.Server/Persistence/DesignDbContextFactory.cs` (mod -> `IDesignTimeDbContextFactory<SqlServerDesignDbContext>`; add Sqlite factory) | config | n/a | itself | exact |
| `src/AzureDesignStudio.Server/Persistence/Migrations/*` (new SQL Server baseline + normalisation) | migration | batch | `DatabaseBootstrapper.LegacySqlServerUpgrade` lines 40-109 | exact (source text) |
| `src/AzureDesignStudio.Server/Persistence/DatabaseBootstrapper.cs` (mod: Migrate, AllowCreate guard, Migrate=false) | service | batch | itself | exact |
| `src/AzureDesignStudio.Server/Persistence/DatabaseProvider.cs` (mod: `AddDesignDbContext`) | utility | config | itself (Configure at 62-81) | exact |
| `src/AzureDesignStudio.Server/Program.cs` (mod: use shared registration) | config | n/a | itself | exact |
| `src/AzureDesignStudio.Desktop/MauiProgram.cs` (mod: WorkspaceStartup, deploy.db Migrate, scheduler, host feature) | config/provider | event-driven | itself lines 60-90, 155-175, 205-212 | exact |
| `src/AzureDesignStudio/Services/AdsHostFeatures.cs` (mod: one new member, e.g. `OpenWorkspaceData`) | provider | request-response | itself, `OpenTenantScan` line 18 | exact |
| `src/AzureDesignStudio.Desktop/Components/WorkspaceDataDrawerTemplate.razor` (new) | component | request-response | `Desktop/Components/TenantScanDrawerTemplate.razor` | exact |
| `src/AzureDesignStudio.Desktop/` scheduler singleton (new, PeriodicTimer) | service | event-driven | none in repo | no analog |
| `src/AzureDesignStudio.Core.Tests/Workspace*Tests.cs` (new: Upgrade, Integrity, Backup, Concurrency, UniqueIndexGuard, MigrationLock, AdsWorkspaceOpen) | test | file-I/O | `Core.Tests/WorkspaceTests.cs` | exact |
| `src/AzureDesignStudio.Core.Tests/Fixtures/*.sql` (new legacy fixtures L1-L4) | test fixture | batch | none | no analog |
| `src/AzureDesignStudio.Server.Tests/Persistence/*` (SqlServerFact, DeployDbMigration) | test | CRUD | `Server.Tests/Hosting/DatabaseBootstrapperTests.cs` | role-match |
| `src/AzureDesignStudio.Server.Tests/Hosting/DatabaseBootstrapperTests.cs` (mod) | test | CRUD | itself (BuildServices 75-89) | exact |
| `src/AzureDesignStudio.Server.Tests/Hosting/SourceGuardTests.cs` (mod) | test | file-I/O | itself 15-70 | exact |
| `.github/workflows/build.yml` (mod: new `sqlserver` job) | config | batch | `build:` job lines 11-40 | role-match |
| `docs/database-operations.md` (new runbook) | docs | n/a | none | no analog |

## Pattern Assignments

### `Workspace/WorkspaceConnection.cs` (utility, request-response)

**Analog:** `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs` lines 84-100 and 187-193, and `src/AzureDesignStudio.Mcp/AdsWorkspace.cs` lines 19-35. Both currently build the connection string by hand with `Cache = Shared`; the factory replaces both.

**Writer side today** (`WorkspaceDbContext.cs` 88-100, to be replaced):
```csharp
public WorkspaceDb(string databasePath)
{
    ConnectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared,   // REMOVE (WAL + shared cache discouraged)
    }.ToString();

    using var db = CreateContext();
    db.Database.EnsureCreated();     // REMOVE -> WorkspaceStartup / Migrate
    EnsureAddedColumns(db);          // MOVE -> LegacyAdoption
    EnsureAdditiveTables();          // MOVE -> baseline migration
}
```

**Context creation** (lines 187-193): keep shape, add `.AddInterceptors(new PragmaInterceptor())`:
```csharp
public WorkspaceDbContext CreateContext()
{
    var options = new DbContextOptionsBuilder<WorkspaceDbContext>()
        .UseSqlite(ConnectionString)
        .Options;
    return new WorkspaceDbContext(options);
}
```

**Reader side today** (`AdsWorkspace.cs` 21-26, `Mode = ReadOnly` + `Cache = Shared`): new reader builder keeps `Mode = ReadOnly`, drops `Cache`. Interceptor/pragma body: copy from 04-RESEARCH.md "Options factory with per-open pragmas" (no in-repo analog for `DbConnectionInterceptor`).

Keep `WorkspaceDb(path)` meaning "open and migrate, fresh file, no backup" so `WorkspaceTests`, `ChangeFeedTests`, `EngineRegressionTests` keep working (RESEARCH Pitfall 5).

---

### `Workspace/Migrations/*Baseline*` (migration, batch)

**Analog / source text:** `WorkspaceDbContext.cs` lines 128-182 is the existing guarded DDL (`CREATE TABLE IF NOT EXISTS`, `CREATE [UNIQUE] INDEX IF NOT EXISTS`). The baseline must cover every table, not only these four: derive the full text from `sqlite_master` of a fresh `EnsureCreated` file (RESEARCH Pattern 2), then wrap in `migrationBuilder.Sql("""...""")`. Excerpt of the shape to carry over:
```csharp
CREATE TABLE IF NOT EXISTS "ChangeFeed" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ChangeFeed" PRIMARY KEY AUTOINCREMENT,
    ...
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChangeFeed_TargetResourceId_ChangeKey" ON "ChangeFeed" ("TargetResourceId", "ChangeKey");
```
Model lines to keep matching the snapshot: `WorkspaceDbContext.cs:27` (`HasIndex(d => d.Name).IsUnique()`), `:63` (ChangeFeed unique), `:74-75` (GovernanceFindings key + index). `Down` stays empty.

---

### `Workspace/LegacyAdoption.cs` + UniqueIndexGuard (utility, batch)

**Analog:** `WorkspaceDbContext.cs` `EnsureAddedColumns` lines 102-118. Move verbatim, but run only when `__EFMigrationsHistory` is absent and `Scans` exists:
```csharp
foreach (var (table, column) in new[] { ("Scans", "ManifestJson"), ("Scans", "CheckpointJson") })
{
    using var check = conn.CreateCommand();
    check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}';";
    if (Convert.ToInt64(check.ExecuteScalar()) == 0)
    {
        using var add = conn.CreateCommand();
        add.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT;";
        add.ExecuteNonQuery();
    }
}
```
Unique-index guard: table-driven, shape per RESEARCH Pattern 5 (no in-repo analog for dedupe/jsonl report). Keys to seed: `Designs(Name)` keep greatest `SavedAt`; `ChangeFeed(TargetResourceId, ChangeKey)` keep greatest `ChangeTime`.

---

### `Workspace/Integrity/WorkspaceIntegrity.cs`, `Backup/*` (utility, file-I/O)

**Analog (raw connection + bound parameter):** `Mcp/AdsWorkspace.cs` lines 38-46:
```csharp
await using var connection = new SqliteConnection(_connectionString);
await connection.OpenAsync();
await using var command = connection.CreateCommand();
command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
command.Parameters.AddWithValue("$name", tableName);
return await command.ExecuteScalarAsync() is not null;
```
Use the same `$name` bound-parameter style for `VACUUM INTO $p`. For `quick_check`, `Pooling=False`, exceptions-as-corruption, and temp-then-rename, copy RESEARCH.md "Integrity check helper" and "Backup with a bound parameter". Call `SqliteConnection.ClearAllPools()` before any file move (existing precedent: `WorkspaceTests.cs:33`). File-I/O pairing with the workspace: `Workspace/LegacyDesignImporter.cs` (file import at startup) for naming/location conventions.

---

### `Mcp/AdsWorkspace.cs` (service, request-response; modify)

**Analog:** itself. Constructor (lines 19-27) builds `_connectionString` with shared cache; replace with the ladder from `WorkspaceConnection.OpenReader(path)` and expose `OpenMode`. Keep `NewContext()` (29-35) and `HasTableAsync` (38-46) unchanged in shape. Keep the class doc rule: never run `WorkspaceDb` bootstrap/migration from MCP. Put the ladder in Workspace (not Mcp) so Core.Tests can test it without referencing Mcp (RESEARCH Wave 0). Report the rung on stderr only; stdout is the protocol channel.

---

### `Services/Models/SqliteDesignDbContext.cs` (model, CRUD)

**Analog:** `src/AzureDesignStudio.Services/Models/DesignDbContext.cs`. Thin derived context: ctor taking `DbContextOptions<SqliteDesignDbContext>` and passing to the base. Must stay provider-free (guard test `ServicesProjectStaysFreeOfWebHostAndProviderPackages` greps the csproj). Generated SQLite migration/snapshot compile in Services; SQL Server ones do not (C1).

### `Server/Persistence/SqlServerDesignDbContext.cs` and `DesignDbContextFactory.cs`

**Analog:** `Server/Persistence/DesignDbContextFactory.cs` lines 18-24:
```csharp
public sealed class DesignDbContextFactory : IDesignTimeDbContextFactory<DesignDbContext>
{
    public DesignDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DesignDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=ads-design-time;Integrated Security=true")
            .Options);
}
```
Change the generic argument to `SqlServerDesignDbContext`/`DbContextOptions<SqlServerDesignDbContext>`; add a sibling `SqliteDesignDbContextFactory` in the Server (the Server is the `dotnet ef` startup for the Services set). Keep the doc remark about no credential in the string. Existing test `DesignTimeFactoryReportsSqlServerAndBuildsTheModelWithoutConnecting` must keep passing.

---

### `Server/Persistence/Migrations/*` SQL Server baseline (migration, batch)

**Analog / source text:** `DatabaseBootstrapper.LegacySqlServerUpgrade` lines 40-109. Move verbatim into the baseline `migrationBuilder.Sql`, preceded by guarded `CREATE TABLE` for `AdsDesigns` (with `CONSTRAINT ADS_CHECK_JSON CHECK (ISJSON(DesignData)=1)`) and `AzureSubscriptions`. Guard idiom to follow for the additions:
```sql
IF OBJECT_ID(N'AdsPlanApprovals', N'U') IS NULL
CREATE TABLE AdsPlanApprovals (
    PlanHash nvarchar(71) NOT NULL PRIMARY KEY,   -- baseline: use CONSTRAINT PK_AdsPlanApprovals PRIMARY KEY
    ...
    ApprovedAtUtc datetime2 NOT NULL DEFAULT GETUTCDATE(),   -- baseline: drop the DEFAULT on the create branch
```
Column-add guard idiom (lines 71-72): `IF OBJECT_ID(...) IS NOT NULL AND COL_LENGTH(N'T', N'C') IS NULL ALTER TABLE ... ADD ...`. Migration 2 dynamic default drop: RESEARCH Pattern 7 (untested, A8).

---

### `Server/Persistence/DatabaseBootstrapper.cs` (service, batch; modify)

**Analog:** itself. Keep the scope/logger preamble (lines 15-23) and provider warnings. Replace lines 25-33:
```csharp
db.Database.EnsureCreated();
if (NeedsLegacyUpgrade(db.Database))
    db.Database.ExecuteSqlRaw(LegacySqlServerUpgrade);
```
with: SqlServer -> `AllowCreate` existence check (`IRelationalDatabaseCreator.Exists()`), `Database:Migrate=false` + pending -> refuse, else `db.Database.Migrate()`; Sqlite/InMemory -> keep the single `EnsureCreated` call so the guard test remains valid. Delete `NeedsLegacyUpgrade` and `LegacySqlServerUpgrade` and move their tests to baseline-content tests.

---

### `Server/Persistence/DatabaseProvider.cs` (modify)

**Analog:** itself, `Configure(options, kind, configuration)` lines 62-81 (UseInMemory / UseSqlite / UseSqlServer branches). Add `AddDesignDbContext(IServiceCollection, DatabaseKind, IConfiguration)` registering `AddDbContext<DesignDbContext, SqlServerDesignDbContext>` (or Sqlite variant) calling `Configure` inside, used by both `Program.cs` and `DatabaseBootstrapperTests.BuildServices` (Pitfall 6). Preserve the "resolve once, carry it" discipline (doc at 53-56).

---

### `Desktop/MauiProgram.cs` (config, event-driven; modify)

**Analog:** itself.
- Lines 60-69: appData resolution with `#if MACCATALYST MacHost.AppDataDirectory`; keep. Replace line 69 `new WorkspaceDb(Path.Combine(appData,"workspace.db"))` with `WorkspaceStartup.Open(...)`, then register the report (notice) and backup service as singletons next to `builder.Services.AddSingleton(workspaceDb);`.
- Lines 78-79: `AddDbContext<DesignDbContext>(options => options.UseSqlite($"Data Source={...deploy.db}"))` -> `AddDbContext<DesignDbContext, SqliteDesignDbContext>`.
- Lines 208-210: replace `Database.EnsureCreated()` with a premigration `VACUUM INTO` backup of `deploy.db` then `Migrate()`:
```csharp
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AzureDesignStudio.Services.Models.DesignDbContext>().Database.EnsureCreated();
```
- Lines 160-175: `AdsHostFeatures` seam registration (`OpenTenantScan` with the `scanDrawer` field close-then-reopen idiom) is the template for the new workspace-data drawer entry.

### `AdsHostFeatures.cs` (modify)

**Analog:** itself, line 18: `public Func<IServiceProvider, Task>? OpenTenantScan { get; set; }` with XML doc "null = feature not present". Add one member beside it; WASM host leaves it null. Possibly a label property like `TenantScanLabel` (line 12) to hide the menu item.

### `Desktop/Components/WorkspaceDataDrawerTemplate.razor` (component)

**Analog:** `Desktop/Components/TenantScanDrawerTemplate.razor` header (lines 1-30): `@inherits FeedbackComponent<string, string>`, `@using AntDesign`, `@using AzureDesignStudio.Workspace`, `@inject ...`, `@implements IDisposable`. Follow AGENTS.md anti-patterns (no `async void`, AntDesign enums not strings, no `await` on void `MessageService.*`).

---

### Tests

**`Core.Tests/Workspace*Tests.cs`** - analog `Core.Tests/WorkspaceTests.cs`: temp dir per test class, `new WorkspaceDb(Path.Combine(_directory, "workspace.db"))` (line 28), `Dispose` calls `Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()` (line 31-33), `Query(db, sql)` helper for schema comparison (used at 283-299 comparing `pragma_table_info` of fresh vs reopened). Rewrite `GovernanceTable_CreatedOnExistingWorkspace` (lines ~280-300; it drops `GovernanceFindings` and expects reopen to recreate) as an L2 legacy-fixture test. Use `pragma_*` comparison, never `sqlite_master.sql`. Raw connections in tests use `Pooling=False`.

**`Server.Tests/Hosting/DatabaseBootstrapperTests.cs`** - `BuildServices` (lines 75-89) registers bare `DesignDbContext` with `DatabaseProvider.Configure`; switch to `DatabaseProvider.AddDesignDbContext`. Assertions style at 100-113 (`Assert.Equal(0, db.X.Count())` after `Initialize`).

**`Server.Tests/Hosting/SourceGuardTests.cs`** lines 23-24 and 43-66: `EnsureCreatedCall` regex, expects exactly one caller `BootstrapperPath`. Update, do not delete: keep single-site rule for `EnsureCreated`; add an analogous `\.Migrate(Async)?\(` guard allowing only bootstrapper, Desktop startup and Workspace startup. Also keep `ServicesProjectStaysFreeOf...` green.

**`SqlServerFactAttribute`** - no in-repo analog; copy RESEARCH.md snippet (FactAttribute subclass setting `Skip` when `ADS_TEST_SQLSERVER` unset) and add `[Trait("Category","SqlServer")]`.

### `.github/workflows/build.yml` (new `sqlserver` job)

**Analog:** `build:` job lines 11-40 (checkout `fetch-depth: 0`, `actions/setup-dotnet@v4` with `global-json-file: global.json`, `dotnet restore ... --locked-mode`). Copy that header; add `services: mssql:` and the TCP wait + `--filter "Category=SqlServer"` from RESEARCH.md. Also add the new `.config/dotnet-tools.json` restore and keep the lock-file tracked-count check (every csproj needs a committed `packages.lock.json`; Workspace and Server locks change, revert SDK-drift rewrites of unrelated locks).

## Shared Patterns

### Raw SQLite access for checks, backups, adoption
**Source:** `Mcp/AdsWorkspace.cs` 38-46 and `WorkspaceDbContext.cs` 102-118. Always a raw `SqliteConnection`/`DbConnection` with bound parameters (`$name`), `pragma_table_info`/`pragma_index_list` for introspection, never string-concat of user paths.
**Apply to:** WorkspaceIntegrity, WorkspaceBackup, LegacyAdoption, UniqueIndexGuard, MCP probe.

### Guarded DDL idempotency
**Source:** SQLite `WorkspaceDbContext.cs` 128-182 (`IF NOT EXISTS`), SQL Server `DatabaseBootstrapper.cs` 41-109 (`IF OBJECT_ID ... IS NULL`, `COL_LENGTH`, `INDEXPROPERTY`).
**Apply to:** both baseline migrations (move text verbatim, change only the listed points).

### Single bootstrap site + provider resolve-once
**Source:** `DatabaseBootstrapper.cs` 13-25 and `DatabaseProvider.cs` 53-81, `SourceGuardTests.cs` 43-66.
**Apply to:** Server bootstrap changes; any new `Migrate()` call sites must be added to the guard's allow-list explicitly.

### Host-neutral Services stays provider-free
**Source:** AGENTS.md + guard test `ServicesProjectStaysFreeOfWebHostAndProviderPackages`. Only SQLite-neutral derived context + SQLite migrations go in Services; SQL Server context and migrations go in Server (C1).

### Host-capability seam
**Source:** `AdsHostFeatures.cs` 18 + `MauiProgram.cs` 160-175. New UI entry = one nullable member + a drawer template in the Desktop assembly; no direct UI coupling from shared code.

### Windows/pool file handling
**Source:** `WorkspaceTests.cs` 31-33 (`SqliteConnection.ClearAllPools()`). Call before any move/replace of db files (quarantine, restore, tests).

## No Analog Found

| File | Role | Data Flow | Reason |
|---|---|---|---|
| `.config/dotnet-tools.json` | config | n/a | No tool manifest exists; use RESEARCH Installation block (move the SDK-written `./dotnet-tools.json` into `.config/`) |
| `Migrations/Sqlite/*` (deploy.db) and generated snapshots | migration | batch | Generated by `dotnet ef`; follow the exact commands in RESEARCH Pattern 7/8 |
| Desktop backup scheduler (`PeriodicTimer` singleton) | service | event-driven | No timer-based hosted service in Desktop; follow RESEARCH Pattern 4 |
| `DbConnectionInterceptor` pragma class | utility | event-driven | No EF interceptor in repo; use RESEARCH Code Example (verified) |
| Legacy fixtures `*.sql` and schema describers | test fixture | batch | No SQL fixtures yet; build from the DDL at `WorkspaceDbContext.cs` 128-182 and git history (RESEARCH Wave 0) |
| `docs/database-operations.md` | docs | n/a | No ops runbook exists |

## Metadata

**Analog search scope:** `src/AzureDesignStudio.Workspace`, `.Mcp`, `.Server/Persistence`, `.Services/Models`, `.Desktop` (MauiProgram, Components), `AzureDesignStudio/Services`, `.Server.Tests/Hosting`, `.Core.Tests/WorkspaceTests.cs`, `.github/workflows/build.yml`
**Files scanned:** ~20 read, ~60 listed
**Pattern extraction date:** 2026-11-14

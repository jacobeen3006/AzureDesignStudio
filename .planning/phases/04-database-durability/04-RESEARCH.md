# Phase 4: Database Durability - Research

**Researched:** 2026-11-14
**Domain:** EF Core 10 migrations over SQLite (desktop workspace, deploy.db, MCP reader) and SQL Server (server), SQLite WAL / backup / integrity, CI container parity
**Confidence:** HIGH on mechanics (almost every load-bearing claim below was reproduced locally against EF Core 10.0.12, Microsoft.Data.Sqlite 10.0.12 and a SQL Server 2022 container); MEDIUM on Mac Catalyst runtime behaviour (not executable in this session) and on production SQL Server hosting details.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**Backup and restore**
- Desktop offers both a manual Backup and Restore command (file picker, `VACUUM INTO`, restore verifies the file with `quick_check` and row counts before swapping it in) and automatic backups in the app data folder (taken before every migration and on a schedule, with a bounded retention) - **Reversibility:** costly - the backup file format and the data-folder layout become something users rely on to restore

**Corruption at startup**
- When `quick_check` fails at startup the Desktop app moves the corrupted file aside (quarantine, never deleted), creates an empty workspace, and shows a notice naming the quarantine path and the available backups so the user can restore - **Reversibility:** costly - a fresh workspace is created without asking; mitigated because the quarantined file and the automatic backups remain on disk

**Unique-index violations during upgrade**
- Back up first, then keep the most recently updated row per key, delete the rest, and write the removed rows to a report file so fresh and upgraded databases end with the same schema - **Reversibility:** one-way - rows are deleted from the live database; undo requires the pre-upgrade backup

**Server (SQL Server) rollback**
- The supported rollback is restoring the pre-upgrade backup, documented as a runbook and rehearsed in the SQL Server container CI job; EF `Down()` methods are generated but are not the supported path - **Reversibility:** costly - defines the operational procedure operators will follow

**Locked by the roadmap implementation note**
- Baseline-as-guarded-SQL: migration 1 is the existing guarded DDL via `migrationBuilder.Sql` for both providers (legacy databases get a history row without re-creation); migrations 2+ are generated diffs - **Reversibility:** one-way - the first migration is the history every installation carries
- `dotnet-ef` 10.0.13, no `Cache=Shared`, WAL with `busy_timeout`, `foreign_keys` and per-connection pragmas set by a `WorkspaceDb` options/pragma factory; MCP opens WAL workspaces read-only (or reports the explicit `ReadWrite + query_only` fallback)
- Checked-in legacy fixtures built from today's bootstrap DDL (workspace SQLite and the SQL Server legacy schema); SQL Server parity runs in its own container job; back up before migrating, never re-create a user database
- Document `__EFMigrationsLock` recovery

### Claude's Discretion (the "Decided autonomously" items)
- [Backups] "a `backups` subfolder of the app data root, newest 5 kept plus every pre-migration backup of the last 30 days" - retention is a constant that can be changed without touching stored data
- [Backups] "`workspace-<UTC yyyyMMddTHHmmssZ>-<reason>.db` where reason is manual, scheduled or premigration" - naming is internal and parsed only by the retention code
- [Quarantine] "`workspace.corrupt-<UTC timestamp>.db` beside the data folder's workspace" - cheap to rename
- [Migrations] "in `AzureDesignStudio.Workspace` for the workspace and in the Services project for the deploy/server context, with design-time factories beside them" - moving files later is a local refactor (**see Correction C1: this one cannot be followed for SQL Server**)
- [Testing] "xUnit tests on temp SQLite files (writer plus reader tasks, a deliberately truncated file) in Core.Tests, SQL Server parity in a separate container job" - test approach is local to the test project

### Deferred Ideas (OUT OF SCOPE)
None - discussion stayed within phase scope.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| DATA-01 | An existing `workspace.db` upgrades through EF migrations with designs, scans, and plans intact and row counts unchanged. | Pattern 2 (guarded-SQL baseline derived from `sqlite_master`, legacy-adoption pre-step for the two `Scans` columns, `Migrate()`), proven on fresh/EnsureCreated/legacy files (Verified Findings F1-F4); fixtures L1-L4; schema-parity describer. |
| DATA-02 | The server database upgrades through EF migrations, and rollback is documented and rehearsed. | Pattern 7 (SQL Server baseline in the Server project, `Migrate()` in `DatabaseBootstrapper`), BACKUP/RESTORE rehearsal and 3-way concurrent `Migrate()` reproduced on SQL Server 2022 (F10-F11); CI container job (Code Examples). |
| DATA-03 | SQLite runs WAL with `busy_timeout` and `foreign_keys`; a concurrent scan write and MCP read does not fail with `SQLITE_BUSY`/`SQLITE_LOCKED`. | Pattern 1 (options + `DbConnectionInterceptor`, no `Cache=Shared`), 15 s two-process stress run with 0 errors (F6), WAL facts (F5, F7). |
| DATA-04 | Startup `quick_check` surfaces workspace corruption instead of silently continuing. | Pattern 3 (startup state machine, quarantine); quick_check behaviour matrix incl. throw-vs-return and blind spots (F8). |
| DATA-05 | A user can create and restore a WAL-safe backup (`VACUUM INTO`), and the restore is verified. | Pattern 4 (parameterised `VACUUM INTO`, temp-then-rename, manifest + SHA-256, staged restore, stale `-wal/-shm` removal), F9. |
| DATA-06 | MCP opens a WAL workspace read-only with an explicit, reported fallback mode and passes a cold-workspace test. | Pattern 6 (open-mode ladder ReadOnly -> ReadWrite+query_only -> immutable), cold/read-only-directory matrix (F7). |
| DATA-08 | The migration baseline adopts existing DDL, reports or dedupes rows that violate new unique indexes, and documents `__EFMigrationsLock` recovery. | Pattern 5 (table-driven unique-index guard + report), lock-hang reproduction and recovery (F12), Correction C2 on which indexes are actually "new". |
</phase_requirements>

## Project Constraints (from AGENTS.md)

No `./CLAUDE.md` exists; `AGENTS.md` is the project instruction file. Directives that bind this phase:

- **Build authority**: package versions only in `Directory.Packages.props`; csproj declares `Include=` only; locked restore is a CI flag (`--locked-mode`), never a repo property; every `src/*/*.csproj` must have a committed `packages.lock.json` (CI counts them). TreatWarningsAsErrors is on; NuGet audit is High/Critical across the full graph.
- **SDK pin**: `global.json` = `"version": "10.0.401", "rollForward": "latestPatch"`. A developer SDK 10.0.403 rewrites unrelated lock files (observed again this session: `src/AzureDesignStudio/packages.lock.json` implicit `10.0.12 -> 10.0.14` pins) - revert that drift, never commit it.
- **`AzureDesignStudio.Services` must stay free of ASP.NET Core and database provider packages** (guard test `ServicesProjectStaysFreeOfWebHostAndProviderPackages` greps the csproj for `Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.AspNetCore`).
- **EnsureCreated guard**: `SourceGuardTests.EnsureCreatedIsCalledOnlyByTheDatabaseBootstrapper` requires exactly one file in Server+Services calling `EnsureCreated`. Update, do not delete, this guard.
- **Desktop storage**: everything under `FileSystem.AppDataDirectory` (Mac: `MacHost.AppDataDirectory`); `workspace.db` and `deploy.db`; designs are local. Desktop is not trimmed on either platform, and Mac Catalyst Release runs with `UseInterpreter=true`.
- **Testing**: `Core.Tests` covers Core/Discovery/Workspace (offline filter `Category!=LiveAzure`); `Server.Tests` calls gRPC services directly and covers hosting. New tests go where the code is reachable (Core.Tests cannot see Services/Server; Server.Tests cannot see Workspace unless a reference is added).
- **Commit conventions**: `docs(<phase>):` for planning artifacts; do not hand-edit `STATE.md` counters.
- **Anti-patterns** that apply: `async void` handlers, awaiting `void` methods (`MessageService.Error`), AntDesign enums not raw strings.

## Summary

The workspace and server schemas are today owned by three mechanisms each (`EnsureCreated`, guarded raw DDL, an `ALTER TABLE` shim), the desktop app additionally owns `deploy.db` through a fourth `EnsureCreated` call, and the SQLite workspace is opened with `Cache=Shared` in both the writer and the MCP reader. The recommended shape is: **EF Core migrations with a guarded-SQL baseline (migration 1) for the SQLite workspace, the SQL Server database, and the desktop `deploy.db`; a single options/pragma factory in the Workspace project that both the desktop writer and the MCP reader use; a startup state machine (integrity check -> pre-migration backup -> legacy adoption -> bounded `Migrate()` -> report); and a `VACUUM INTO` backup/restore service with a manifest.** Every mechanism was reproduced in throwaway projects and a SQL Server 2022 container during this session; the key results are recorded as Verified Findings F1-F14.

Four findings change the plan and contradict inputs (see "Corrections" below). Most importantly, **EF 10's SQL Server model snapshot and designer code reference `SqlServerModelBuilderExtensions`/`SqlServerPropertyBuilderExtensions`, so SQL Server migrations cannot compile in the provider-free Services project**; they must live in the Server project. SQLite migrations do compile in Services, which keeps the CONTEXT decision valid for the desktop `deploy.db`. The two unique indexes the workspace model declares have been created by the shipped DDL since the day each table was introduced, so the dedupe feature is a defensive pre-flight plus a mechanism for future indexes, not a repair for a known population of broken files.

**Primary recommendation:** Land the work in three independent tracks - (A) Workspace: `WorkspaceConnection` factory + `Migrations/` baseline + legacy adoption + integrity/backup/restore services + MCP open modes; (B) Server: `SqlServerDesignDbContext` + SQL Server baseline in the Server project + `Migrate()` in `DatabaseBootstrapper` + runbook + SQL Server container CI job; (C) Desktop: startup wiring, scheduler, one `AdsHostFeatures` seam + drawer, and `deploy.db` on a `SqliteDesignDbContext` migration set - with tooling (tool manifest, `Microsoft.EntityFrameworkCore.Design`) and legacy fixtures as Wave 0.

### Corrections to CONTEXT / roadmap / earlier research (read before planning)

| # | Input said | Reality (verified) | Consequence |
|---|-----------|--------------------|-------------|
| C1 | Migration classes for the server context live in the Services project (CONTEXT, autonomous decision) | Generated SQL Server snapshot/designer code uses `SqlServerModelBuilderExtensions` and `SqlServerPropertyBuilderExtensions`; compiling it in Services fails with CS0103 (16 errors). The SQLite equivalent compiles in Services. [VERIFIED: local build in a scratch worktree of the repo, `dotnet build src/AzureDesignStudio.Services`] | SQL Server migrations go in `AzureDesignStudio.Server` (it already references the provider and owns `DesignDbContextFactory`). SQLite `deploy.db` migrations may stay in Services. Services' provider-free guard test stays green. |
| C2 | `.planning/research/PITFALLS.md` Pitfall 4: the shipped `ChangeFeed` DDL had "no unique index", so field databases are weaker than the model | Every shipped version that created `ChangeFeed` also created the unique index; `IX_Designs_Name` has been unique since the first workspace commit. [VERIFIED: `git show` of commits 013bdf9, 5536611, d23531e, 2b33750 and the current file, quoted in F13] | No known population of field databases violates a "new" unique index. DATA-08's dedupe is a defensive, table-driven pre-flight, exercised by a doctored fixture, and a reusable mechanism for the Phase 5 tenant indexes. Do not size it as a repair campaign. |
| C3 | WAL must be enabled (roadmap SC3) | Databases created by EF's `EnsureCreated`/`Migrate()` are already `journal_mode=wal`: the developer's real macOS `workspace.db`, `deploy.db` and freshly created scratch files all answer `wal`. [VERIFIED: `sqlite3 PRAGMA journal_mode` on those files] A file produced by `VACUUM INTO` is `delete` mode, so a restore must re-assert WAL. | Phase work is: assert WAL explicitly at open, remove `Cache=Shared` (today WAL + shared cache is already the live combination), add the per-connection pragmas, and re-assert after a restore. |
| C4 | Fallback "`ReadWrite + query_only`" makes MCP robust when read-only fails | In a read-only *directory* both `ReadOnly` and `ReadWrite+query_only` fail with `SQLITE_READONLY_DIRECTORY` (ext 1544); only `immutable=1` opens it. [VERIFIED: local experiment F7] `ReadWrite+query_only` helps for the WAL-recovery case (a read-only connection cannot run recovery), not for permissions. | Implement a three-step ladder and report which rung was used; `immutable=1` only when no non-empty `-wal` exists. |
| C5 | `quick_check` is the corruption gate | It *throws* (codes 11 and 26) for truncation and header damage, returns `ok` for a 0-byte file, and returned `ok` for mid-file data damage that `integrity_check` caught. [VERIFIED: F8] | Treat any exception from the check as corruption; treat a 0-byte main file with a non-empty `-wal` as suspicious; use `integrity_check` for restore verification and the explicit "verify" action. |

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Workspace schema authority (migrations, legacy adoption) | Desktop host library (`AzureDesignStudio.Workspace`) | - | The SQLite file belongs to the desktop process; MCP must never run it (it is read-only by construction). |
| SQLite options + pragmas (WAL, busy_timeout, FK) | `AzureDesignStudio.Workspace` (one factory) | MCP reader (consumes the factory's read-only variant) | Single seam prevents a third `DbContextOptionsBuilder` (ARCHITECTURE.md seam rule). |
| Integrity check, quarantine, backup, restore, retention | `AzureDesignStudio.Workspace` services (pure logic, unit-tested in Core.Tests) | Desktop host (scheduling, file picker, notice UI) | Logic must be testable without MAUI; only pickers/timers/UI are host concerns. |
| Backup/restore UI and corruption notice | Desktop host (Razor drawer in the Desktop assembly) | Shared UI via one `AdsHostFeatures` member | Same pattern as `OpenTenantScan`; the WASM host leaves the member null. |
| MCP open mode ladder and reporting | `AzureDesignStudio.Mcp` (`AdsWorkspace`) | Workspace factory | Reads only; reports the rung it used on stderr and via a property. |
| Server schema authority (SQL Server) | API/Backend host (`AzureDesignStudio.Server`: context, migrations, `DatabaseBootstrapper`) | Database (SQL Server) | Provider-specific migration code must compile against the SqlServer provider (C1). |
| Desktop `deploy.db` schema authority | `AzureDesignStudio.Services` (SQLite migration set on `SqliteDesignDbContext`) | Desktop host calls `Migrate()` | SQLite snapshots compile without a provider; keeps Services provider-free. |
| Server rollback | Operations runbook + SQL Server backup/restore | CI container job rehearses it | Locked: restore of the pre-upgrade backup, not `Down()`. |
| SQL Server parity testing | CI (own job with a service container) | Server.Tests (`SqlServerFact` tests) | Locked: separate container job. |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `Microsoft.EntityFrameworkCore.Sqlite` | 10.0.12 (already central) | Workspace/deploy.db provider and `Migrate()` | Already in use; bundles `e_sqlite3` (SQLite 3.53.3 at runtime in this session, so `VACUUM INTO` (3.27+) is available). [VERIFIED: `select sqlite_version()` -> 3.53.3] |
| `Microsoft.EntityFrameworkCore.SqlServer` | 10.0.12 (already central) | Server provider | Already in use. |
| `Microsoft.EntityFrameworkCore.Design` | **10.0.12** (add to `Directory.Packages.props`; reference only from `AzureDesignStudio.Server` and `AzureDesignStudio.Workspace` with `PrivateAssets=all`) | Design-time services for `dotnet ef` | Required by the tool in the startup project. Restores with no audit warning and changes only the referencing project's lock file (Server lock +149 lines). [VERIFIED: restore in scratch worktree; `git diff --stat`] Match the EF runtime version (10.0.12). |
| `dotnet-ef` (local tool) | **10.0.13** via `.config/dotnet-tools.json` | `migrations add/remove/script`, `has-pending-model-changes` | Locked decision. 10.0.13 tool against 10.0.12 packages produced no version warning and generated both SQLite and SQL Server sets. [VERIFIED: local run, SDK 10.0.403] |
| xUnit | 2.9.2 (already central) | All tests | Existing. Dynamic skip is done with a `FactAttribute` subclass (no new package; xUnit 2 has no `Assert.Skip`). |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `Microsoft.Data.Sqlite` (transitive of the EF Sqlite provider) | 10.0.12 | Raw connections for `quick_check`, `VACUUM INTO`, row counts, MCP probe | Anywhere the check must not go through EF (pre-migration, pre-restore). |
| `System.Security.Cryptography` (BCL) | in-box | SHA-256 of backup files for the manifest | Never hand-roll hashing. |
| Microsoft.Data.SqlClient (transitive of the SqlServer provider) | via EF 10.0.12 | `BACKUP/RESTORE` rehearsal in tests | No direct package reference needed in Server.Tests; it flows through the Server project reference. [VERIFIED: scratch project built with only the EF SqlServer package] |
| `CommunityToolkit.Maui` | 17.0.0 (**optional, not recommended without confirmation**) | `FileSaver`/`FolderPicker` for "Save backup as..." | MAUI Essentials has `FilePicker` (open) but no save/folder picker. See Open Question 3. |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Derived context per provider (`SqlServerDesignDbContext`, `SqliteDesignDbContext`) | One context + `MigrationsAssembly` per provider (two assemblies) | EF documents both. Derived contexts keep both sets generated from one project each with `--context`, avoid the "two `IDesignTimeDbContextFactory<DesignDbContext>` in one startup project" ambiguity, and were verified end-to-end. The two-assembly route needs a new project for the SQLite set because Desktop cannot be a `dotnet ef` startup. [CITED: learn.microsoft.com/ef/core/managing-schemas/migrations/providers] |
| Runtime `Migrate()` for SQL Server | `dotnet ef migrations bundle` as an init container | Bundle is the documented preference for HA/least-privilege. Production runs **one replica** today (`replicas: 1`, comment: the what-if token gate is in-process), EF 9+ takes a DB-wide lock, and 3 concurrent `Migrate()` calls were verified safe. Start with runtime `Migrate()` behind a config switch; revisit before raising replicas. [VERIFIED: deployment.yaml:13; F11] |
| `SqliteConnection.BackupDatabase` | `VACUUM INTO` | Locked: `VACUUM INTO`. It also compacts (625 MB from 687 MB in the test) and is a consistent snapshot including uncheckpointed WAL commits. |
| Testcontainers.MsSql | GitHub Actions `services:` container | Testcontainers adds a package and a Docker dependency to every dev run; a service container is zero-package and the job is already separate. |

**Installation:**
```bash
dotnet new tool-manifest                      # creates the manifest (this SDK wrote ./dotnet-tools.json; move it to .config/dotnet-tools.json)
dotnet tool install dotnet-ef --version 10.0.13
# Directory.Packages.props: add <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12" />
# Server.csproj + Workspace.csproj: <PackageReference Include="Microsoft.EntityFrameworkCore.Design"><PrivateAssets>all</PrivateAssets><IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets></PackageReference>
```

**Version verification:** `dotnet-ef` and `Microsoft.EntityFrameworkCore.Design` list 10.0.0 ... 10.0.15 on nuget.org (flat container index, queried 2026-11-14); 10.0.13 and 10.0.12 both exist. EF Core 11.0.0 is also published but the project targets net10.0, so stay on 10.0.x. [VERIFIED: api.nuget.org/v3-flatcontainer]

## Package Legitimacy Audit

The `gsd-tools package-legitimacy check` seam only accepts `npm|pypi|crates` (it prints `Usage: ... --ecosystem <npm|pypi|crates>`), so it could not be run for NuGet. Registry metadata was checked directly instead; verdicts below are therefore **manual**, not seam-issued, and are tagged `[CITED]`, not `[VERIFIED: npm registry]`.

| Package | Registry | Age | Downloads | Source Repo | Verdict | Disposition |
|---------|----------|-----|-----------|-------------|---------|-------------|
| Microsoft.EntityFrameworkCore.Design | NuGet | EF Core packages since 2016 | 1.15 B total; owners `aspnet, dotnetframework, EntityFramework, Microsoft`, verified | github.com/dotnet/efcore | OK (manual) [CITED: azuresearch-usnc.nuget.org query 2026-11-14; learn.microsoft.com/ef/core/cli/dotnet] | Approved |
| dotnet-ef (tool) | NuGet | since 2016 | 139 M total; owners `aspnet, dotnetframework, Microsoft`, verified | github.com/dotnet/efcore | OK (manual) [CITED: same query; learn.microsoft.com/ef/core/cli/dotnet] | Approved |
| CommunityToolkit.Maui | NuGet | .NET Foundation | 10.7 M total; owners `dotnetfoundation, Microsoft.Toolkit`, verified | github.com/CommunityToolkit/Maui | OK (manual) but **not recommended without user confirmation** (A5) [ASSUMED fit for Mac Catalyst + Windows save dialogs] | Optional; planner must add `checkpoint:human-verify` before adding it |

**Packages removed due to [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** none. All three are first-party/foundation packages; neither new required package appears anywhere outside Microsoft's EF distribution.

## Architecture Patterns

### System Architecture Diagram

```
                       DESKTOP PROCESS (MAUI, Windows / Mac Catalyst)                      OTHER PROCESSES
 ┌────────────────────────────────────────────────────────────────────────────────────┐
 │ MauiProgram.CreateMauiApp                                                          │
 │   resolve appData ──► WorkspaceStartup.Open(path, backupsDir)                      │
 │        │                    │                                                       │
 │        │      ┌─────────────▼──────────────┐                                        │
 │        │      │ 1 exists? ─ no ──────────────────► fresh: Migrate() from zero      │
 │        │      │ 2 quick_check (raw conn,    │                                        │
 │        │      │   exception == corrupt)     │                                        │
 │        │      │    fail ─► QUARANTINE db+wal+shm ► fresh workspace + Notice(path, backups)
 │        │      │ 3 legacy? (no history)      │                                        │
 │        │      │   or pending migrations?    │                                        │
 │        │      │    yes ─► VACUUM INTO backups/workspace-<utc>-premigration.db(+manifest)
 │        │      │ 4 legacy adoption (only when no history table):                      │
 │        │      │    add Scans.ManifestJson/CheckpointJson; unique-index guard         │
 │        │      │    (dedupe keep-newest, write report)                                │
 │        │      │ 5 Migrate()  ── bounded wait (30 s) ──► timeout = "abandoned lock"  │
 │        │      │ 6 assert PRAGMA journal_mode=WAL (persistent)                        │
 │        │      └─────────────┬──────────────┘                                        │
 │        │                    ▼                                                       │
 │        │         WorkspaceDb (options factory: no Cache=Shared, interceptor        │
 │        │                      applies busy_timeout, foreign_keys on EVERY open)     │
 │        ▼                    │                                                       │
 │  DI: WorkspaceDb, WorkspaceStartupReport(Notice), WorkspaceBackupService           │
 │       │                         │                                                   │
 │       │  scheduler (PeriodicTimer, 24 h) ──► VACUUM INTO ...-scheduled.db + prune  │
 │       │  Drawer: Backup now / list / Restore ► stage candidate ► verify ► swap on  │
 │       │          next launch (step 0 of the flow above)                             │
 │  deploy.db: SqliteDesignDbContext.Migrate()  (backup reason premigration)           │
 └───────────────┬───────────────────────────────────────────────────────────────────┘
                 │ workspace.db (+ -wal, -shm)              ┌───────────────────────────┐
                 └──────────────────────────────────────────►│ MCP server (ads-mcp)      │
                                                             │ WorkspaceConnection.      │
                                                             │  OpenReader(path):        │
                                                             │  ReadOnly ► RW+query_only │
                                                             │  ► immutable (no -wal)    │
                                                             │  reports the rung used    │
                                                             └───────────────────────────┘

                       SERVER PROCESS (ASP.NET Core, one replica)
 Program.cs ► DatabaseBootstrapper.Initialize
   SqlServer:  Migrate() (EF DB-wide lock)   baseline = guarded T-SQL, then generated diffs
   Sqlite/InMemory (dev/test only): unchanged EnsureCreated (single remaining call site)
 Operator: BACKUP DATABASE ► deploy image ► (Migrate runs) ► on failure RESTORE ... WITH REPLACE
 CI: own job + mssql service container ► SqlServerFact tests (fresh vs legacy parity, row counts,
     backup/migrate/restore rehearsal, concurrent Migrate)
```

### Recommended Project Structure
```
.config/dotnet-tools.json                           # dotnet-ef 10.0.13
src/AzureDesignStudio.Workspace/
├── Migrations/                                     # 20261114xxxxxx_Baseline.cs (guarded SQL), snapshot, later diffs
├── WorkspaceConnection.cs                          # options builders (writer / reader), PragmaInterceptor, open-mode ladder
├── WorkspaceDbContext.cs                           # model + WorkspaceDb (slimmed: no DDL, no EnsureCreated)
├── WorkspaceDbContextFactory.cs                    # IDesignTimeDbContextFactory (temp file path)
├── WorkspaceStartup.cs                             # state machine -> WorkspaceStartupReport
├── LegacyAdoption.cs                               # missing columns + UniqueIndexGuard
├── Integrity/WorkspaceIntegrity.cs                 # quick_check/integrity_check, quarantine
└── Backup/{WorkspaceBackup,BackupManifest,BackupRetention}.cs
src/AzureDesignStudio.Services/
├── Models/SqliteDesignDbContext.cs                 # derived context shell (provider-free)
└── Migrations/Sqlite/                              # deploy.db baseline + snapshot
src/AzureDesignStudio.Server/Persistence/
├── SqlServerDesignDbContext.cs                     # derived context shell (default migrations assembly = Server)
├── Migrations/                                     # SQL Server baseline (guarded T-SQL) + snapshot + normalisation migration
├── DesignDbContextFactory.cs                       # now IDesignTimeDbContextFactory<SqlServerDesignDbContext>
└── DatabaseBootstrapper.cs                         # Migrate() for SqlServer; EnsureCreated only for Sqlite/InMemory
src/AzureDesignStudio.Desktop/                      # startup wiring, scheduler, WorkspaceDataDrawerTemplate.razor
src/AzureDesignStudio.Core.Tests/Workspace*/        # fixtures (*.sql), upgrade/parity/backup/corruption/concurrency tests
src/AzureDesignStudio.Server.Tests/Persistence/     # SqlServerFact tests, SQLite deploy.db migration tests, guard updates
docs/database-operations.md                         # runbook: SQLite lock recovery, restore, SQL Server backup/rollback
```

### Pattern 1: One options/pragma factory, no `Cache=Shared`
**What:** `WorkspaceConnection` builds the connection string (`DataSource`, `Mode`, `Pooling=True`, `Foreign Keys=True`, no `Cache`) and attaches a `DbConnectionInterceptor` that runs `PRAGMA busy_timeout=5000` (and re-asserts `foreign_keys=ON`) in `ConnectionOpened`/`ConnectionOpenedAsync`. `WorkspaceDb.CreateContext()` and `AdsWorkspace.NewContext()` both call it. WAL is asserted once per process start (it is persistent in the file), not per open.
**When to use:** every workspace connection, including tests.
**Why this way:** `Microsoft.Data.Sqlite` has a `Default Timeout` keyword that is the *command* timeout and retries busy/locked internally until it elapses, but there is no `busy_timeout` keyword, so the pragma needs a hook. [CITED: learn.microsoft.com/dotnet/standard/data/sqlite/connection-strings, .../database-errors] Microsoft states mixing shared cache and WAL is discouraged. [CITED: same connection-strings page] `foreign_keys` is already `1` by default with the bundled `e_sqlite3` (raw connection returned `1`), so setting it is determinism, not behaviour change. [VERIFIED: local experiment; CITED: connection-strings page "SQLITE_DEFAULT_FOREIGN_KEYS"]
**Do not set `synchronous=NORMAL`** (earlier research suggested it): in WAL mode "A transaction committed in WAL mode with synchronous=NORMAL might roll back following a power loss". This is the Durability phase and users' saved designs live here; keep the default FULL unless a measured scan-write cost forces otherwise. [CITED: sqlite.org/pragma.html#pragma_synchronous]
**Example:** see Code Examples (verified 15 s, two-process stress, 0 errors).

### Pattern 2: Baseline-as-guarded-SQL for SQLite, derived from the real schema
**What:** Generate `Baseline` with `dotnet ef migrations add`, then replace `Up` with one `migrationBuilder.Sql` containing the DDL that a fresh `EnsureCreated` produces - read from `sqlite_master` of a freshly created file - rewritten to `CREATE TABLE IF NOT EXISTS` / `CREATE [UNIQUE] INDEX IF NOT EXISTS`. Keep the generated `Designer.cs` and `ModelSnapshot` untouched (they describe the model; `HasPendingModelChanges()` stays `false`). `Down` is empty (Down is not the supported path).
**Verified behaviour:** Fresh file -> `Migrate()` yields the same schema as `EnsureCreated`; an `EnsureCreated`-built file adopts the baseline (history row, no re-creation); a legacy file with row data keeps every row. [VERIFIED: F1, F2]
**What the guarded SQL cannot do:** SQLite has no conditional `ALTER TABLE ... ADD COLUMN`. A legacy `Scans` table without `ManifestJson`/`CheckpointJson` "migrates successfully" and stays broken (F3). Therefore the **legacy-adoption pre-step** runs *before* `Migrate()` only when `__EFMigrationsHistory` is absent and `Scans` exists: it executes the existing `EnsureAddedColumns` logic. ALTER ADD appends at the end, matching the fresh column order, so `pragma_table_info` is identical afterwards. [VERIFIED: F3]
**Pre-existing drift to know about:** tables created by the guarded DDL (`NodePositions`, `DecomWindows`) have an *unnamed* primary key; the model's `EnsureCreated` form names it `PK_<Table>`. `pragma_*` output is identical, only `sqlite_master.sql` text differs - so the "same schema" test must compare `pragma_table_info`, `pragma_index_list` (origin `c`) and `pragma_foreign_key_list`, never raw `sql`. EF's table rebuilds regenerate the table from the model, so later `AlterColumn` migrations normalise it. [VERIFIED: schema dump + F4]

### Pattern 3: The workspace startup state machine
Order matters and each step must be unit-testable without MAUI (`WorkspaceStartup.Open(path, backupsDir, clock, options) -> WorkspaceStartupReport`):
0. Apply a staged restore if one is pending (Pattern 4).
1. If the main file is missing -> fresh create (no backup).
2. `quick_check` on a raw `Pooling=False`, `Mode=ReadOnly` connection; **any `SqliteException` is corruption**; a 0-byte main file with a non-empty `-wal` is also corruption (F8). On failure: `SqliteConnection.ClearAllPools()`, move `workspace.db`, `-wal`, `-shm` to `workspace.corrupt-<utc>.db` (+ `-wal`/`-shm` with the same stem), then continue as fresh and record `Notice(QuarantinePath, Backups[])`.
3. If the DB exists and (no history table or `GetPendingMigrations().Any()`) -> `VACUUM INTO` a `premigration` backup + manifest; abort the upgrade (do not migrate) if the backup fails or free disk < 1.2 x file size.
4. Legacy adoption (only when no history table): add missing columns; unique-index guard.
5. `MigrateAsync(ct)` with a 30 s `CancellationToken`. An abandoned `__EFMigrationsLock` makes `Migrate` wait forever (F12); the timeout converts that into an actionable error naming `DELETE FROM "__EFMigrationsLock"` and the backup path. Never delete lock rows silently.
6. Assert `PRAGMA journal_mode=WAL`; run `quick_check` once more only if migrations were applied (cheap: 229 ms on 687 MB).
A failed upgrade is **not** corruption: do not quarantine; the migration transaction rolled back atomically (SQLite DDL is transactional), the backup exists, surface the error and the backup path.

### Pattern 4: Backup, manifest, retention, staged restore
- **Create:** `VACUUM INTO $path` with a **bound parameter** (a path containing an apostrophe worked; no string concatenation), written to `<name>.tmp`, then `quick_check` the result, hash it (SHA-256), write `<name>.manifest.json` (`createdUtc`, `reason`, `appVersion`, `latestMigrationId`, `tableCounts{}`, `sha256`), then rename `.tmp` -> final. `VACUUM INTO` fails if the target exists and "might be incomplete and corrupt" if interrupted, hence temp-then-rename. [VERIFIED: F9; CITED: sqlite.org/lang_vacuum.html#vacuuminto] The result is a consistent snapshot including uncheckpointed WAL commits and is `journal_mode=delete`. [VERIFIED: F9]
- **Names/retention (CONTEXT):** `workspace-<yyyyMMddTHHmmssZ>-<manual|scheduled|premigration>.db`. Implement retention as a **pure function** `BackupRetention.Plan(IReadOnlyList<BackupFile>, DateTimeOffset now) -> (keep, delete)`: keep the newest 5, plus every `premigration` backup newer than 30 days. Recommendation A3: also keep every `manual` backup (a user-initiated backup silently pruned would be a surprise; this is a constant-level change).
- **Scheduled:** `PeriodicTimer(24 h)` in a Desktop singleton; also run once at startup when the newest `scheduled`/`premigration` backup is older than 24 h. A reader transaction does not block WAL writers, so it is safe during a scan (F7).
- **Restore (staged):** (1) the user picks a file with MAUI `FilePicker` (open dialog; Windows and Mac Catalyst supported, no save/folder dialog exists in Essentials [CITED: learn.microsoft.com/dotnet/maui/platform-integration/storage/file-picker]); copy the stream into `<appData>/restore-staging/` (the picker may return a temp copy). (2) Verify on a raw read-only connection with `trusted_schema=OFF`: `PRAGMA integrity_check` (1.8 s per 687 MB; a superset of the locked `quick_check` - recommendation), schema allow-list (no `trigger`/`view` objects, only known tables), `__EFMigrationsHistory` max id is known to this build (refuse a newer schema), row counts vs the manifest when present, SHA-256 vs manifest. (3) Write `restore.pending.json`. (4) On the next launch, *before* opening any connection: `ClearAllPools()`, move the live `workspace.db`+`-wal`+`-shm` to `workspace.pre-restore-<utc>.db`, move the staged file into place, **delete stale `-wal`/`-shm`** (a leftover WAL from the old file can be replayed onto the restored one), assert WAL, run the normal state machine (which migrates an older backup forward). A hot swap is rejected: scans, MCP and pooled connections hold the file.
- **Windows specifics:** replacing a file while MCP holds it fails with `IOException`; catch it and tell the user to stop `ads-mcp`. On macOS a rename succeeds but the running MCP keeps the old inode until it reopens.

### Pattern 5: Table-driven unique-index guard (DATA-08)
`UniqueIndexGuard` takes `(table, indexName, keyColumns, keepOrderBy)`; for the two existing unique indexes: `Designs(Name)` keep greatest `SavedAt`, `ChangeFeed(TargetResourceId, ChangeKey)` keep greatest `ChangeTime` (ties: greatest rowid). It runs only for tables that exist and only when the named unique index is **not** already present (`pragma_index_list`, `unique=1`). Query: `SELECT keycols, COUNT(*) FROM t GROUP BY keycols HAVING COUNT(*) > 1`; for each group keep one row, delete the rest in a transaction, append each removed row (all columns) as one JSON object per line to `<backups>/upgrade-removed-rows-<utc>.jsonl`. Back up first (Pattern 3 step 3). Because C2 shows no real file should trigger it, the test is a **doctored fixture** (L4): tables created *without* the unique indexes and duplicate rows inserted. The guard is also the hook Phase 5 reuses for any tenant-scoped unique index.

### Pattern 6: MCP open-mode ladder (DATA-06)
`WorkspaceConnection.OpenReader(path) -> ReaderOpen(Mode, ConnectionString, Detail)`:
1. `Mode=ReadOnly` (no `Cache`), `Pooling=True`, run `PRAGMA busy_timeout=5000` and a `SELECT 1 FROM sqlite_master LIMIT 1` probe.
2. On `SQLITE_READONLY`(8)/`SQLITE_CANTOPEN`(14)/`SQLITE_READONLY_RECOVERY`: `Mode=ReadWrite` + `PRAGMA query_only=ON` (reports `ReadWriteQueryOnly`). `query_only` blocks CREATE/INSERT/UPDATE/DELETE/DROP but "the database is not truly read-only. You can still run a checkpoint". [CITED: sqlite.org/pragma.html#pragma_query_only]
3. If still `SQLITE_READONLY_DIRECTORY` and no non-empty `<db>-wal` exists: `file:<abs path>?immutable=1` (reports `Immutable`; may be stale if a writer later starts). Otherwise fail with a message naming the directory permission.
`AdsWorkspace` exposes `OpenMode`; `Program.cs` writes it to stderr at startup (stdout is the protocol channel). Do not add a 16th MCP tool just for this (AGENTS.md documents "15 read-only tools"); optionally add a caveat string to existing responses. Also read `__EFMigrationsHistory`'s latest id and report if it is newer than this build knows (the reader never migrates; additive changes stay readable because EF only selects mapped columns).
**Cold-workspace test:** file exists, `-wal`/`-shm` absent (`PRAGMA wal_checkpoint(TRUNCATE)` then close with `Pooling=False`), writable directory -> `ReadOnly` succeeds and creates `-shm`/`-wal`; read-only directory -> rung 3. [VERIFIED: F7]

### Pattern 7: SQL Server - baseline, bootstrapper, rollback, CI
- **Context type:** `SqlServerDesignDbContext(DbContextOptions<SqlServerDesignDbContext>) : DesignDbContext` defined **in the Server project**, so the default migrations assembly is the Server. Register with `AddDbContext<DesignDbContext, SqlServerDesignDbContext>` (services keep asking for `DesignDbContext`). `DesignDbContextFactory` becomes `IDesignTimeDbContextFactory<SqlServerDesignDbContext>` (existing test `DesignTimeFactoryReportsSqlServerAndBuildsTheModelWithoutConnecting` keeps working). Generate with `dotnet ef migrations add <Name> --context SqlServerDesignDbContext --project src/AzureDesignStudio.Server --startup-project src/AzureDesignStudio.Server --output-dir Persistence/Migrations`. [VERIFIED: ran in a scratch worktree of the real tree, TreatWarningsAsErrors on, `has-pending-model-changes` reports none]
- **Baseline `Up`:** one `migrationBuilder.Sql` = guarded `CREATE TABLE` for `AdsDesigns` and `AzureSubscriptions` (these two were created by `EnsureCreated`, never by the legacy string; incl. `CONSTRAINT ADS_CHECK_JSON CHECK (ISJSON(DesignData)=1)`) + the existing `LegacySqlServerUpgrade` text (moved verbatim except the points below). Both tables' models have been unchanged since inception (ISJSON and column set present in `0021727`/`d9659ce`, checked via `git show`). [VERIFIED: git]
- **Make the fresh path model-exact, keep the legacy path untouched:** in the `CREATE TABLE` branches of the five legacy tables use named PKs (`CONSTRAINT PK_AdsPlanApprovals PRIMARY KEY`, ...) and drop `DEFAULT GETUTCDATE()` on `AdsPlanApprovals.ApprovedAtUtc`; existing tables are skipped by the `IF OBJECT_ID ... IS NULL` guard. Measured with the verbatim legacy text: fresh-by-baseline vs legacy-upgraded differ **only** in system-generated constraint names (`PK__AdsPlanA__5E0AF07D...`, `DF__...`) and the default constraint; columns, types, nullability and indexes were identical. [VERIFIED: F10]
- **Migration 2 (recommended):** drop the legacy unnamed default dynamically (it is unknown to EF and would block a future `AlterColumn` on that column). Untested syntax -> A8:
  ```sql
  DECLARE @df sysname = (SELECT d.name FROM sys.default_constraints d JOIN sys.columns c
      ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
      WHERE d.parent_object_id = OBJECT_ID(N'AdsPlanApprovals') AND c.name = N'ApprovedAtUtc');
  IF @df IS NOT NULL EXEC(N'ALTER TABLE AdsPlanApprovals DROP CONSTRAINT ' + QUOTENAME(@df));
  ```
- **Bootstrapper:** `SqlServer` -> `db.Database.Migrate()` (delete `LegacySqlServerUpgrade`, `NeedsLegacyUpgrade` and their two tests, moving the guarded-DDL assertions onto the baseline). `Sqlite`/`InMemory` (documented dev/test providers) keep the single `EnsureCreated` call site so the existing guard test stays valid. Add `Database:Migrate` (default `true`); when `false`, refuse to start while `GetPendingMigrations()` is non-empty and print the command - this is the switch for a future bundle/init-container flow.
- **Do not let `Migrate()` invent a database:** `Migrate()` creates the target database when it is missing (SQLite file creation verified; SQL Server behaviour A7). For SqlServer, check `IRelationalDatabaseCreator.Exists()` first and refuse unless `Database:AllowCreate=true`, so a typo in the connection string cannot yield a silent empty database ("never re-create a user database").
- **Rollback (locked):** runbook = (1) take a backup/copy (`BACKUP DATABASE ... WITH CHECKSUM, INIT`; for Azure SQL the portal PITR / `CREATE DATABASE ... AS COPY OF` - A4), (2) `RESTORE VERIFYONLY`, (3) deploy, (4) on failure `ALTER DATABASE SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE ... WITH REPLACE; ... MULTI_USER` and roll back the image. The whole sequence was run against SQL Server 2022: pre-upgrade `2` rows / no history -> post-upgrade `3` rows / history -> post-restore `2` rows / no history -> re-migrate OK. [VERIFIED: F11] `BACKUP ... TO DISK = N'/var/opt/mssql/data/<db>-pre.bak'` is writable in the official container. [VERIFIED: F11]
- **Runtime identity:** `Migrate()` needs DDL rights on the app login. Today's `EnsureCreated` + `CREATE TABLE` already requires them, so this is not a regression; record it in the runbook as accepted until a bundle flow with a separate deployment identity exists. [CITED: learn.microsoft.com/ef/core/managing-schemas/migrations/applying "Use a separate identity for deployment"]

### Pattern 8: Desktop `deploy.db` (recommended in scope; see Open Question 1)
`SqliteDesignDbContext : DesignDbContext` lives in Services; its migration set (`Migrations/Sqlite`) compiles without any provider package (verified) so the provider-free guard stays green. Baseline = EF's SQLite DDL for the seven tables rendered as `CREATE TABLE IF NOT EXISTS` (one script generated by `GenerateCreateScript()` on a SQLite context). Desktop registers `AddDbContext<DesignDbContext, SqliteDesignDbContext>`, replaces `EnsureCreated()` at `MauiProgram.cs:210` with `Migrate()` after a `premigration` `VACUUM INTO` backup of `deploy.db` (same helper, reason in the name, no user-facing restore UI). The `AzureSubscriptions` table holds `ClientSecret`: backups of `deploy.db` contain whatever that column holds, so keep them out of the user-visible backup list and out of "manual" (Security Domain). Generate with `--context SqliteDesignDbContext --project src/AzureDesignStudio.Services --startup-project src/AzureDesignStudio.Server --output-dir Migrations/Sqlite` (Server hosts the SQLite design-time factory `SqliteDesignDbContextFactory`). [VERIFIED: generated and compiled in the scratch worktree]

### Anti-Patterns to Avoid
- **Calling `EnsureCreated()` before/instead of `Migrate()`** on any database that will be migrated: it never writes `__EFMigrationsHistory`. [CITED: learn.microsoft.com/ef/core/managing-schemas/migrations/applying "Don't call EnsureCreatedAsync() before MigrateAsync()"]
- **`BeginTransaction(deferred: true)` for read-then-write** on a WAL database: it fails immediately with extended code 517 (`SQLITE_BUSY_SNAPSHOT`) when another writer committed in between, and Microsoft.Data.Sqlite then retried for the full 30 s command timeout before throwing. [VERIFIED: F5] EF's default transactions are not deferred (a plain `BeginTransaction()` took the write lock at BEGIN; the other writer waited 841 ms and succeeded), which is why `WorkspaceScans.PersistSourcePageAsync` (read first, then write) is safe. Never switch the workspace to deferred transactions.
- **Copying `workspace.db` as a backup** (loses commits still in `-wal`) or restoring without deleting stale `-wal/-shm`.
- **Letting MCP or the pragma factory write**: the reader must never run migrations or `journal_mode=WAL`.
- **Comparing `sqlite_master.sql` text** for schema parity (names of PK constraints differ, see Pattern 2).
- **A hot restore** while scans/MCP/pools hold the file.
- **Hand-editing `STATE.md`/committing SDK-drifted lock files.**

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Schema versioning/history | A custom `SchemaVersion` table or `PRAGMA user_version` scheme | EF migrations + `__EFMigrationsHistory` | EF 9+ lock, pending-model-change detection, SQL generation per provider. |
| Concurrent-migration protection | A lock file or mutex | EF's migration lock (`sp_getapplock` on SQL Server, `__EFMigrationsLock` table on SQLite) | 3 concurrent SQL Server `Migrate()` calls: all `ok`, 1 history row. [VERIFIED: F11] |
| SQLite table rebuilds in later migrations | Hand-written copy/rename SQL | EF's SQLite rebuild for `AlterColumn`/`AddForeignKey`/`DropColumn` | Verified: a rebuild of the parent `Scans` table kept all child `ResourceSnapshots` (2 -> 2) with `foreign_keys` on by default and with it off. [VERIFIED: F4] Only works for model artifacts, so keep every table in the model. [CITED: learn.microsoft.com/ef/core/providers/sqlite/limitations] |
| Consistent online copy of a WAL database | `File.Copy` of db(+wal) | `VACUUM INTO` with a bound parameter | Snapshot incl. uncheckpointed commits (52 rows = 1 + 50 + 1). [VERIFIED: F9] |
| Integrity check | A custom page walker | `PRAGMA quick_check` / `integrity_check` | Built in; mind the blind spots (F8). |
| File hashing | Custom checksum | `SHA256.HashDataAsync` | BCL. |
| Busy retry loop | Own retry/backoff around `SaveChanges` | `busy_timeout` pragma + Microsoft.Data.Sqlite's built-in retry until `Default Timeout` | 2 writers + 2 governance writers + 2 reader threads in another process: 2970 writes / 4.49 M reads / 0 errors in 15 s. [VERIFIED: F6] |
| SQL Server backup in tests | Copying data files | T-SQL `BACKUP DATABASE`/`RESTORE DATABASE` inside the container | Official path, `WITH CHECKSUM`, `RESTORE VERIFYONLY`. |
| Dynamic test skipping | A new skip package | `class SqlServerFactAttribute : FactAttribute` setting `Skip` when `ADS_TEST_SQLSERVER` is unset | xUnit 2.9.2 has no runtime skip API. [ASSUMED: standard xUnit v2 idiom] |
| Retention bookkeeping | Ad-hoc deletes in the UI | One pure `BackupRetention.Plan` function (tiny, unit-tested) | The only "hand-rolled" piece; it is arithmetic, not infrastructure. |

**Key insight:** every durability primitive here already exists in SQLite, EF or T-SQL; the work is ordering (check -> back up -> adopt -> migrate -> assert) and refusing to improvise when something is off. The risk is never "can SQLite do it", it is "which file/lock/journal state were we in".

## Runtime State Inventory

This phase renames nothing, but it is a data-migration phase over live user data, so the five questions are answered explicitly:

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | `workspace.db` (Designs, Scans, ResourceSnapshots, CostCache, ExecutionPlans, AuditRecords, NodePositions, ChangeFeed, DecomWindows, GovernanceFindings), `deploy.db` (7 `Ads*`/`AzureSubscriptions` tables), server `ads-main` SQL Server DB (same 7 tables). The developer's own macOS files exist at `~/Library/Application Support/com.azuredesignstudio.desktop/` (no `GovernanceFindings` table, 0 designs, 0 scans - a genuine pre-governance legacy shape). [VERIFIED: `sqlite3` read of that file] | Data migration = baseline adoption (history row + missing columns); code edit = baseline/model. Do **not** mutate the developer's real files in tests; copy with `VACUUM INTO` for a manual rehearsal. |
| Live service config | SQL Server connection string `ads-main` in Key Vault; k8s `replicas: 1` in `src/Configurations/main/deployment.yaml`. | No rename. Add config keys `Database:Migrate`, `Database:AllowCreate`. Runbook must state replicas stay 1 until a bundle flow exists. |
| OS-registered state | None - verified by reading `MauiProgram.cs`/`Platforms/*`: no scheduled tasks or launch agents; the backup scheduler is an in-process timer. | None. |
| Secrets/env vars | None renamed. New test-only `ADS_TEST_SQLSERVER` (CI service-container connection string). `deploy.db`'s `AzureSubscriptions.ClientSecret` rides into any `deploy.db` backup. | Keep `deploy.db` backups out of the user-facing list; document. |
| Build artifacts | `src/*/packages.lock.json` (Server gains ~149 lines; Workspace gains its own); `.config/dotnet-tools.json` is new; macOS has a separate `packages.maccatalyst.lock.json` (Desktop only; unaffected because `PrivateAssets=all`). | Commit only intended lock changes; revert SDK-patch drift. |

## Common Pitfalls

### Pitfall 1: The guarded baseline "succeeds" but leaves columns missing (F3)
**What goes wrong:** legacy `Scans` has no `ManifestJson`/`CheckpointJson`; `Migrate()` records the baseline and the next scan write throws `no such column`.
**Why:** `CREATE TABLE IF NOT EXISTS` is a no-op on an existing table; SQLite has no `ADD COLUMN IF NOT EXISTS`.
**How to avoid:** the legacy-adoption pre-step (Pattern 2) *plus* a fixture test (L1) that asserts the columns exist after upgrade.
**Warning signs:** parity diff shows only two missing columns; a scan on an upgraded file fails.

### Pitfall 2: `Migrate()` hangs forever on an abandoned `__EFMigrationsLock` (F12)
**What goes wrong:** the process is killed mid-migration; every later start blocks at startup with no UI.
**How to avoid:** `MigrateAsync` with a 30 s token, error text with the recovery statement; document recovery: close the app, `sqlite3 workspace.db "DELETE FROM \"__EFMigrationsLock\";"` (or `DROP TABLE "__EFMigrationsLock"`), restart; the table is recreated as needed. [VERIFIED: stuck for the full 8 s test window, then `migrated in 62ms` after the DELETE; CITED: learn.microsoft.com/ef/core/providers/sqlite/limitations#concurrent-migrations-protection] SQL Server uses a session lock (`sp_getapplock`) that is released with the connection, so no table to clean there. The lock table has columns `Id`, `Timestamp`.

### Pitfall 3: `quick_check` is not a full check, and it throws (F8)
Truncated file -> exception code 11; zeroed header / non-DB text -> code 26; 0-byte file -> `ok` (it is a valid empty database); 64 bytes of 0xFF in the middle of the data -> `quick_check` = `ok`, `integrity_check` = `row 272124 missing from index ix`. "The pragma ... does not verify UNIQUE constraints and does not verify that index content matches table content." [CITED: sqlite.org/pragma.html#pragma_quick_check] The deliberately-corrupted test fixture for SC4 must therefore be a truncation or header overwrite, not a bit flip; and the explicit verify/restore path should use `integrity_check`.

### Pitfall 4: Pending-model-changes turns a forgotten migration into a startup crash
EF 9+ makes `Migrate()` throw `PendingModelChangesWarning` when the model differs from the snapshot. [CITED: learn.microsoft.com/ef/core/managing-schemas/migrations/applying "Migration locking" note] Add an in-process test per context: `Assert.False(db.Database.HasPendingModelChanges())` (the API exists in 10.0.12 and returned `False` on the current model). [VERIFIED: local run] It needs no tool restore in CI. Phase 5's first model edit will trip it - that is the point.

### Pitfall 5: The existing test `GovernanceTable_CreatedOnExistingWorkspace` encodes the old mechanism
It drops `GovernanceFindings` from an up-to-date file and expects reopening to recreate it (`WorkspaceTests.cs:288`, `await db.Database.ExecuteSqlRawAsync("""DROP TABLE "GovernanceFindings";""")`). With migrations the baseline will not re-run on a file that already has a history row. Rewrite it as a legacy-fixture test (L2: no `GovernanceFindings` table, no history) asserting creation, identical columns/indexes to a fresh file, and untouched rows. Other `new WorkspaceDb(path)` callers in Core.Tests (`WorkspaceTests`, `ChangeFeedTests`, `EngineRegressionTests`) can keep working if the constructor keeps meaning "open and migrate" (fresh file, no backup).

### Pitfall 6: Registering the derived context breaks the existing bootstrapper tests
`DatabaseBootstrapperTests.BuildServices` registers a bare `DesignDbContext` with `DatabaseProvider.Configure`. Migrations are attributed to the derived types, so `Migrate()` on a bare `DesignDbContext` applies nothing. Extract one `DatabaseProvider.AddDesignDbContext(services, kind, configuration)` used by both `Program.cs` and the tests so they cannot drift (the same "resolve once, carry it" discipline Phase 3 applied to `databaseKind`). The `LegacyUpgrade*` tests move to baseline-content tests.

### Pitfall 7: Schema-parity tests that compare constraint names on SQL Server
Fresh-by-baseline and legacy-upgraded differ in system-generated names (`PK__...__<8 hex>`, `DF__...__<8 hex>`). Normalise with `^(PK|DF)__.+__[0-9A-F]{8}$` -> `<system-name>` and compare columns/types/nullability/indexes; assert the one known default-constraint difference until migration 2 removes it. [VERIFIED: F10]

### Pitfall 8: Windows file locking in tests and restore
Pooled connections keep files open: the existing tests already call `SqliteConnection.ClearAllPools()` in `Dispose`; the restore/quarantine code must do the same before moving files, and tests that open raw connections should use `Pooling=False`.

### Pitfall 9: WAL growth while a reader churns (F6)
After the 15 s stress run the `-wal` was 20 MB because the reader loop never let a checkpoint complete (SQLite documents this). Real MCP reads are short, but set `PRAGMA journal_size_limit` (e.g. 64 MB) and run `PRAGMA wal_checkpoint(TRUNCATE)` after a scan completes (it returned busy=1 with a reader open - run it opportunistically, never fail on it). [CITED: sqlite.org/wal.html#ckpt; sqlite.org/pragma.html#pragma_wal_checkpoint]

### Pitfall 10: Don't let the migration path touch the user's real data in tests
Fixtures are `.sql` scripts executed into temp files; never point a test at `~/Library/Application Support/com.azuredesignstudio.desktop/`.

## Code Examples

### Options factory with per-open pragmas (verified: two-process stress, 0 errors)
```csharp
// Source: local experiment (scratchpad conc2); pattern per Microsoft.Data.Sqlite docs
public sealed class PragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection c, ConnectionEndEventData e) => Apply(c);
    public override Task ConnectionOpenedAsync(DbConnection c, ConnectionEndEventData e, CancellationToken ct = default)
    { Apply(c); return Task.CompletedTask; }
    static void Apply(DbConnection c)
    {
        using var k = c.CreateCommand();
        k.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";   // NOT synchronous=NORMAL (durability)
        k.ExecuteNonQuery();
    }
}
// writer: new SqliteConnectionStringBuilder { DataSource = path, Pooling = true, ForeignKeys = true }  // no Cache
// reader: + Mode = SqliteOpenMode.ReadOnly
// options: new DbContextOptionsBuilder<WorkspaceDbContext>().UseSqlite(cs).AddInterceptors(new PragmaInterceptor())
```

### Baseline migration (SQLite) - derive, don't retype
```csharp
// 20261114xxxxxx_Baseline.cs  (Designer.cs + ModelSnapshot stay as generated)
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.Sql("""
        CREATE TABLE IF NOT EXISTS "Scans" ( ... exact text from a fresh file's sqlite_master ... );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Designs_Name" ON "Designs" ("Name");
        ... one statement per table/index, tables first ...
        """);
}
protected override void Down(MigrationBuilder migrationBuilder) { }   // restore-from-backup is the supported rollback
```
Generate the text with: create a fresh file via `EnsureCreated` on the model, `SELECT sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'` (tables, then indexes), prefix `IF NOT EXISTS`. Add a test that fails if the baseline text and the model diverge: `HasPendingModelChanges()` is false **and** the fresh-vs-`EnsureCreated` schema describer is equal.

### Schema describer for parity tests (SQLite)
```csharp
// Compare logical schema, never sqlite_master.sql (PK constraint names differ by origin).
foreach (var t in tables)
  foreach (var q in new[] {
    $"select cid,name,type,\"notnull\",dflt_value,pk from pragma_table_info('{t}') order by cid",
    $"select name,\"unique\",origin,partial from pragma_index_list('{t}') where origin='c' order by name",
    $"select \"table\",\"from\",\"to\",on_delete from pragma_foreign_key_list('{t}')" }) { ... }
```

### Integrity check helper (exception == corruption)
```csharp
// Source: local experiment F8
static IntegrityResult Check(string path, string pragma = "quick_check")
{
    try {
        using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"); c.Open();
        using var k = c.CreateCommand(); k.CommandText = $"PRAGMA {pragma}";
        var lines = new List<string>(); using var r = k.ExecuteReader(); while (r.Read()) lines.Add(r.GetString(0));
        return lines is ["ok"] ? IntegrityResult.Ok : IntegrityResult.Failed(lines);
    } catch (SqliteException e) { return IntegrityResult.Failed([$"code={e.SqliteErrorCode} {e.Message}"]); }   // 11 malformed, 26 not a database
}
```

### Backup with a bound parameter
```csharp
// Source: local experiment F9 (path "…/O'Brien/backup.db" worked; existing target -> 'output file already exists')
using var c = new SqliteConnection($"Data Source={live};Pooling=False"); c.Open();
using var k = c.CreateCommand(); k.CommandText = "VACUUM INTO $p"; k.Parameters.AddWithValue("$p", tmpPath); k.ExecuteNonQuery();
// then Check(tmpPath), SHA256, manifest, File.Move(tmpPath, finalPath)
```

### Bounded migrate
```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try { await db.Database.MigrateAsync(cts.Token); }
catch (OperationCanceledException) { throw new WorkspaceUpgradeException(
    "The workspace upgrade did not finish. A previous upgrade may have been interrupted (abandoned __EFMigrationsLock). " +
    "Close the app, run: DELETE FROM \"__EFMigrationsLock\"; then start again. Backup: " + backupPath); }
```

### SQL Server fact + CI job
```csharp
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute() { if (Environment.GetEnvironmentVariable("ADS_TEST_SQLSERVER") is null) Skip = "ADS_TEST_SQLSERVER not set"; }
}
```
```yaml
# .github/workflows/build.yml - new job, independent of `build` (Linux runner is amd64, no emulation)
  sqlserver:
    runs-on: ubuntu-latest
    services:
      mssql:
        image: mcr.microsoft.com/mssql/server:2022-latest
        env: { ACCEPT_EULA: "Y", MSSQL_SA_PASSWORD: "Ads-Ci-Throwaway-1!" }   # ephemeral test credential, not a secret
        ports: [ "1433:1433" ]
    env:
      ADS_TEST_SQLSERVER: "Server=127.0.0.1,1433;User ID=sa;Password=Ads-Ci-Throwaway-1!;TrustServerCertificate=True;Encrypt=False;"
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0 }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: dotnet restore src/AzureDesignStudio.Server.Tests --locked-mode
      - run: dotnet build src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false
      - name: Wait for SQL Server
        run: for i in $(seq 1 60); do (echo > /dev/tcp/127.0.0.1/1433) 2>/dev/null && break; sleep 2; done
      - run: dotnet test src/AzureDesignStudio.Server.Tests -c Release --no-build -p:RunAOTCompilation=false --filter "Category=SqlServer"
```
The image tags `2022-latest` and `2025-latest` both resolve (`docker manifest inspect`). [VERIFIED] A readiness probe by TCP port avoids depending on the `sqlcmd` path inside the image (A9); tests should additionally retry the first connection (SqlClient `ConnectRetryCount`) because TCP-open precedes login readiness.

### Rehearsal assertions (server, run inside the container)
```sql
BACKUP DATABASE [ads_rehearsal] TO DISK = N'/var/opt/mssql/data/ads_rehearsal-pre.bak' WITH INIT, CHECKSUM, FORMAT;
RESTORE VERIFYONLY FROM DISK = N'/var/opt/mssql/data/ads_rehearsal-pre.bak' WITH CHECKSUM;
-- Migrate(); insert a row; then:
ALTER DATABASE [ads_rehearsal] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [ads_rehearsal] FROM DISK = N'/var/opt/mssql/data/ads_rehearsal-pre.bak' WITH REPLACE, CHECKSUM;
ALTER DATABASE [ads_rehearsal] SET MULTI_USER;   -- also SqlConnection.ClearAllPools() before the restore
```

## Verified Findings (local experiments, 2026-11-14)

| # | Finding | Evidence |
|---|---------|----------|
| F1 | Fresh `Migrate()` with a guarded-SQL baseline yields the same logical schema as `EnsureCreated`; history row `20261114105208_Baseline 10.0.12`. | `A migrated ... B equal to A before Migrate? True` |
| F2 | An `EnsureCreated`-built file adopts the baseline via `Migrate()`: history row written, no failure. | `B migrate OK; history: ..._Baseline` |
| F3 | Legacy file whose `Scans` lacks the two columns: `Migrate()` succeeds and leaves them missing; schema diff = exactly `ManifestJson`, `CheckpointJson`. | `C migrate OK ... scans cols: Id,...,Error` / `diff: 9|ManifestJson..., 10|CheckpointJson...` |
| F4 | A generated `AlterColumn` migration rebuilds `Scans` on an adopted legacy file and keeps child rows (2 -> 2) with foreign keys on (default) and off. Raw connection `PRAGMA foreign_keys` = `1` by default. | `snapshots after rebuild = 2` |
| F5 | WAL: deferred read-then-write fails with ext. code 517 after the full 30 s retry; non-deferred writers wait (841 ms) and succeed; read-only reader coexists with a writer. | wal experiment output |
| F6 | Two-process stress (2 page writers, 2 governance writers; reader process with 2 threads), 15 s, no `Cache=Shared`, interceptor pragmas: `writes=2970`, `reads=4491915`, `errors=0`; `-wal` 20 MB afterwards. The *current* shared-cache in-process configuration also showed 0 errors (it is not cross-process, so it proves nothing about MCP). | conc / conc2 output |
| F7 | Cold read-only open in a writable directory succeeds and creates `-shm`/`-wal`; in a read-only directory `ReadOnly` and `ReadWrite+query_only` fail with code 8 / ext 1544; `immutable=1` opens. Checkpoint(TRUNCATE) with an open reader returns busy=1, log=50, ckpt=0. | wal experiment output |
| F8 | `quick_check` on 687 MB: 229 ms (`integrity_check` 1779 ms). Truncated -> exception 11; zeroed header / text file -> exception 26; 0-byte -> `ok`; mid-file 64-byte overwrite -> `quick_check` `ok`, `integrity_check` reports a missing index row. | corr output |
| F9 | `VACUUM INTO $p` works with a bound parameter and a path containing `'`; fails on an existing target; backup included uncheckpointed rows (52), is `journal_mode=delete`, `quick_check=ok`, 625 MB from 687 MB in 865 ms. | corr/wal output |
| F10 | SQL Server 2022 (container, amd64 emulated): baseline `Migrate()` on an empty database and on a populated legacy database (2 designs, 1 approval) both succeed; legacy rows unchanged; schemas differ only by system-generated constraint names and the legacy default constraint; `EnsureCreated` vs baseline differ in the same ways. | sqlmig output |
| F11 | BACKUP -> RESTORE VERIFYONLY -> `Migrate()` -> insert -> RESTORE WITH REPLACE returns to the pre-upgrade state (`2|0` -> `3|1` -> `2|0` -> `2|1`); three concurrent `Migrate()` calls: all succeed, 1 history row. | sqlmig output |
| F12 | An abandoned `__EFMigrationsLock` row makes `MigrateAsync` hang until cancelled; `DELETE FROM "__EFMigrationsLock"` restores a 62 ms migrate. | run output |
| F13 | Unique indexes: `IX_Designs_Name` in the model since commit 2b33750; `ChangeFeed` table and `CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChangeFeed_TargetResourceId_ChangeKey" ON "ChangeFeed" ("TargetResourceId", "ChangeKey");` shipped together in commit 013bdf9. | `git show` greps |
| F14 | EF Design 10.0.12 + dotnet-ef 10.0.13: derived-context pattern generated SQLite set (compiles in Services) and SQL Server set (compiles only in Server) in the real repo tree with TreatWarningsAsErrors; `has-pending-model-changes` clean. A class-library project works as its own `dotnet ef` startup. | scratch worktree runs |

In-repo values quoted (read with the Read tool this session):
- `WorkspaceDbContext.cs:93` `Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared,` and `:97` `db.Database.EnsureCreated();`, `:106` `foreach (var (table, column) in new[] { ("Scans", "ManifestJson"), ("Scans", "CheckpointJson") })`, `:152` `CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChangeFeed_TargetResourceId_ChangeKey" ON "ChangeFeed" ("TargetResourceId", "ChangeKey");`, `:27` `e.HasIndex(d => d.Name).IsUnique();`, `:63` `e.HasIndex(c => new { c.TargetResourceId, c.ChangeKey }).IsUnique();`
- `AdsWorkspace.cs:24-25` `Mode = SqliteOpenMode.ReadOnly,` / `Cache = SqliteCacheMode.Shared,`
- `MauiProgram.cs:69` `var workspaceDb = new WorkspaceDb(System.IO.Path.Combine(appData, "workspace.db"));`, `:78-79` `options.UseSqlite($"Data Source={System.IO.Path.Combine(appData, "deploy.db")}")`, `:210` `...DesignDbContext>().Database.EnsureCreated();`
- `DatabaseBootstrapper.cs:25` `db.Database.EnsureCreated();`, `:32-33` `if (NeedsLegacyUpgrade(db.Database)) db.Database.ExecuteSqlRaw(LegacySqlServerUpgrade);`
- `Directory.Packages.props:39-43` EF packages all `Version="10.0.12"`; `global.json:3-4` `"version": "10.0.401", "rollForward": "latestPatch"`; `deployment.yaml:13` `replicas: 1`.
- `Mcp/Program.cs:44-46` default path `Environment.SpecialFolder.LocalApplicationData` + `"Azure Design Studio", "AzureDesignStudio.Desktop", "Data", "workspace.db"` - on macOS this does not match `MacHost.AppDataDirectory` (`~/Library/Application Support/<bundle id>`); DATA-07 (Phase 11) owns discovery, so MCP users on macOS pass `--db` for now.

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `EnsureCreated` + ad-hoc DDL | EF migrations with a guarded baseline | this phase | One schema authority per database. |
| Unprotected concurrent migrations | EF Core 9+ DB-wide migration lock; `Migrate()` throws on pending model changes | EF 9 | `__EFMigrationsLock` is now an operational concept on SQLite. [CITED: learn.microsoft.com/ef/core/managing-schemas/migrations/applying] |
| Idempotent scripts for rollout | Not available on SQLite; use `database update`/runtime `Migrate()` | long-standing | Reason a procedural baseline is impossible on SQLite. [CITED: learn.microsoft.com/ef/core/providers/sqlite/limitations] |
| Shared cache for "concurrency" | WAL, no shared cache | SQLite guidance | "Mixing shared-cache mode and write-ahead logging is discouraged." [CITED: learn.microsoft.com/dotnet/standard/data/sqlite/connection-strings] |
| `dotnet ef` global tool 6.0.x | Local tool manifest pinned to 10.0.13 | this phase | No global-tool drift; matches EF 10 model reading. |

**Deprecated/outdated:** the `#if DEBUG` provider choice (removed in Phase 3); `LegacySqlServerUpgrade` as a startup string (moves into the baseline).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The Desktop (Mac Catalyst Release, AOT+interpreter, no trimming) can run `Migrate()` at startup. Reflection-based migration discovery should be fine with no trimming and the interpreter, but nothing was executed on a Catalyst build here. | Pattern 3 | Desktop on macOS would fail at launch; mitigate with a `checkpoint:human-verify` task that launches the built `.app` against a copy of the L2 fixture. |
| A2 | `deploy.db` should be brought under migrations in this phase (it is the fourth `EnsureCreated`, and Phase 5's tenant columns on guardrail/approval tables land in it too). REQUIREMENTS DATA-02 only names "the server database". | Pattern 8 | Scope creep if the user wants it deferred; the work is a self-contained last wave and can be dropped without affecting the rest. |
| A3 | Manual backups are exempt from retention pruning. CONTEXT's rule is "newest 5 + premigration last 30 days". | Pattern 4 | A user's manual backup is deleted by the pruner. |
| A4 | The production database may be Azure SQL rather than a self-hosted SQL Server (connection string comes from Key Vault; hosting is not stated anywhere read). The runbook's first step differs (`BACKUP DATABASE` vs PITR/`AS COPY OF`). | Pattern 7 | Operators follow the wrong backup step. |
| A5 | A "Save backup as..." dialog needs `CommunityToolkit.Maui` (Essentials has only an open-file picker). Fallback: write manual backups to the `backups/` folder and offer "Reveal". | Standard Stack | Either an unwanted new package or a UX narrower than CONTEXT's "file picker" wording. |
| A6 | Microsoft.Data.Sqlite 10 reading offset-less timestamps as UTC (earlier research) does not alter legacy rows because EF always wrote offset-bearing `DateTimeOffset` text. Not re-verified. | Pitfalls | Subtle instant shifts; add one fixture round-trip assertion on `Scans.StartedAt`, `ChangeFeed.ChangeTime`, `DecomWindows.UpdatedAtUtc`. |
| A7 | `Migrate()` on SQL Server creates a missing database when the login may. SQLite creation was verified; SQL Server not. | Pattern 7 | If `Migrate()` fails instead, the `AllowCreate` guard is merely redundant. |
| A8 | The dynamic `DROP CONSTRAINT` snippet for the legacy default is syntactically right (not executed). | Pattern 7 | Migration 2 fails on SQL Server; caught by the container job. |
| A9 | `/opt/mssql-tools18/bin/sqlcmd` exists in `mssql/server:2022-latest` (only matters if someone prefers a `--health-cmd` over the TCP wait shown). | Code Examples | Use the TCP wait instead. |
| A10 | `ADS_TEST_SQLSERVER`-gated `FactAttribute` subclass is the right skip idiom for xUnit 2.9.2. | Don't Hand-Roll | Tests run (and fail) without a server on developer machines. |
| A11 | Running `quick_check` synchronously at startup is acceptable; 229 ms at 687 MB. Multi-GB workspaces would stall the splash. | Pattern 3 | Add a size threshold + async path if workspaces grow. |
| A12 | Restore swaps the file on the **next launch** (staged) rather than live. | Pattern 4 | UX requires a restart; a live swap would need a quiesce protocol for scans/MCP. |

## Open Questions

1. **Include `deploy.db` in Phase 4?** (A2)
   - What we know: it is the fourth `EnsureCreated`; the instruction set for this research asked about it; Phase 5 must add tenant columns to tables that also live in it.
   - What's unclear: whether the user wants it now (the roadmap SC text covers `workspace.db` and "the server database" only).
   - Recommendation: include as the final, separable wave (Pattern 8); if deferred, state in the Phase 5 plan that its migration depends on adding this set first.

2. **Production hosting of `ads-main` (Azure SQL vs SQL Server) and who may run DDL.** (A4)
   - Recommendation: write the runbook with both backup variants; keep `Database:Migrate` default `true` while `replicas: 1`.

3. **Manual backup destination UX.** (A5)
   - Recommendation: default to the `backups/` folder + "Reveal in Finder/Explorer"; restore uses `FilePicker`. Add `CommunityToolkit.Maui` only if the user insists on a Save dialog (needs a `checkpoint:human-verify` and a legitimacy re-check).

4. **What should the app do when the *upgrade* (not the integrity check) fails?**
   - What we know: the migration rolled back atomically; the pre-migration backup exists; quarantine is wrong because the file is not corrupt.
   - Recommendation: block with a notice naming the backup and the error text (including the lock-recovery statement when the timeout fired); do not start with a half-functional workspace.

5. **Should `Database:AllowCreate` default to `false` for SQL Server?**
   - Recommendation: yes (A7); document the one-time first-deploy exception in the runbook.

6. **`synchronous` setting.** Recommendation: leave FULL (default); revisit only with a measured scan-write cost.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | everything | yes | 10.0.403 (global.json 10.0.401 + `latestPatch`) | - |
| `dotnet-ef` 10.0.13 | migration generation | yes (installed in scratch via manifest) | 10.0.13 | - |
| SQLite engine (bundled `e_sqlite3`) | all SQLite work | yes | 3.53.3 at runtime | - |
| `sqlite3` CLI | manual inspection only | yes | 3.54.0 | any SQLite browser |
| Docker (OrbStack) | local SQL Server rehearsal | yes | 29.8.2; `mssql/server:2022-latest` ran under amd64 emulation on arm64 | CI service container only |
| SQL Server container image | `SqlServerFact` tests | yes (`2022-latest`, `2025-latest` manifests resolve) | 2022 (tested) | skip locally via env var |
| MAUI Mac Catalyst workload | Desktop build on this Mac | yes | `maui-maccatalyst` 10.0.110 | CI Windows job builds Desktop |
| Windows runner | Desktop build/test of Windows paths | CI only (`windows-latest`, non-blocking) | - | - |
| Node | existing JS tests | yes | - | not needed by this phase |
| `gsd-tools package-legitimacy` for NuGet | audit | **no** (npm/pypi/crates only) | - | manual registry check (done) |

**Missing dependencies with no fallback:** none.
**Missing dependencies with fallback:** NuGet legitimacy seam -> manual nuget.org check; local SQL Server only via emulated container -> CI job is the authority.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit 2.9.2 (`Microsoft.NET.Test.Sdk` 17.12.0) |
| Config file | none (convention); traits via `[Trait("Category", ...)]` |
| Quick run command | `dotnet test src/AzureDesignStudio.Core.Tests --filter "FullyQualifiedName~Workspace|FullyQualifiedName~Backup|FullyQualifiedName~Integrity|FullyQualifiedName~Mcp"` |
| Full suite command | `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` and `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false` (SQL Server job adds `--filter "Category=SqlServer"` with `ADS_TEST_SQLSERVER`) |

### Phase Requirements -> Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| DATA-01 | L1/L2/L3 legacy fixtures upgrade; per-table row counts unchanged; fresh vs upgraded `pragma_*` description equal; `Scans` has both columns; instants round-trip (A6) | integration (temp SQLite) | `dotnet test src/AzureDesignStudio.Core.Tests --filter "FullyQualifiedName~WorkspaceUpgradeTests"` | Wave 0 (new; rewrites `GovernanceTable_CreatedOnExistingWorkspace`) |
| DATA-01 | `HasPendingModelChanges()` false for `WorkspaceDbContext` | unit | `...--filter "FullyQualifiedName~WorkspaceMigrationModelTests"` | Wave 0 |
| DATA-02 | SQL Server: fresh == legacy-upgraded (normalised), rows kept, history written, BACKUP/migrate/RESTORE rehearsal, 3x concurrent `Migrate()`, baseline text contains every legacy table | integration (container) | `dotnet test src/AzureDesignStudio.Server.Tests --filter "Category=SqlServer"` | Wave 0 (new) |
| DATA-02 | Bootstrapper: SqlServer -> Migrate, `Database:Migrate=false` + pending -> refuses, `AllowCreate` guard; `HasPendingModelChanges()` false for both server contexts | unit/integration (SQLite for the non-SQL parts) | `dotnet test src/AzureDesignStudio.Server.Tests --filter "FullyQualifiedName~DatabaseBootstrapper"` | exists; extend |
| DATA-02 (deploy.db) | `SqliteDesignDbContext` migrates an `EnsureCreated`-built SQLite file (all 7 tables) and an old file missing newer tables | integration | `...Server.Tests --filter "FullyQualifiedName~DeployDbMigration"` | Wave 0 |
| DATA-03 | writers + governance writers + out-of-context reader (separate connections, plus one real second-process smoke) 15 s: zero BUSY/LOCKED; `journal_mode=wal`, `busy_timeout`, `foreign_keys` asserted on a **pooled** second connection | integration | `...Core.Tests --filter "FullyQualifiedName~WorkspaceConcurrencyTests"` | Wave 0 |
| DATA-04 | truncated file and zeroed header -> report + quarantine (db, -wal, -shm moved, fresh created, notice names path + backups); 0-byte-with-WAL suspicious; healthy file not touched | integration | `...--filter "FullyQualifiedName~WorkspaceIntegrityTests"` | Wave 0 |
| DATA-05 | backup is consistent with uncheckpointed WAL rows, manifest counts + SHA-256 correct, apostrophe path, existing target refused, retention plan (pure), restore verification rejects: corrupt, newer schema, trigger/view objects, mismatched counts; staged restore deletes stale `-wal/-shm` and re-asserts WAL | unit + integration | `...--filter "FullyQualifiedName~WorkspaceBackupTests"` | Wave 0 |
| DATA-06 | ladder: cold writable dir -> ReadOnly; read-only dir -> Immutable (skip on Windows if ACL setup differs); simulated recovery case -> ReadWriteQueryOnly; `OpenMode` reported; MCP never writes (`query_only` blocks INSERT) | integration | `...--filter "FullyQualifiedName~AdsWorkspaceOpenTests"` (needs Mcp reference or the ladder in Workspace) | Wave 0 |
| DATA-08 | L4 doctored fixture: duplicates reported + deduped keep-newest, removed rows in the `.jsonl` report, backup taken first, indexes created, fresh == upgraded; abandoned-lock fixture: bounded `Migrate` raises the recovery message, `DELETE` then succeeds | integration | `...--filter "FullyQualifiedName~UniqueIndexGuardTests|FullyQualifiedName~MigrationLockTests"` | Wave 0 |

### Sampling Rate
- **Per task commit:** the quick run command above (< 30 s; all tests use temp SQLite files, no network).
- **Per wave merge:** full Core.Tests offline suite + Server.Tests (SQLite/InMemory parts).
- **Phase gate:** both suites green locally, SQL Server container job green in CI, plus the human checkpoints below, before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `.config/dotnet-tools.json`, `Directory.Packages.props` Design version, Server + Workspace `PackageReference`, regenerated locks (Server, Workspace only; revert SDK drift)
- [ ] Legacy fixtures as `.sql` scripts + loader: `workspace-legacy-L1-oldest.sql`, `-L2-pre-governance.sql`, `-L3-current-legacy.sql`, `-L4-duplicates.sql` (Core.Tests); `sqlserver-legacy.sql` (Server.Tests) - each with seeded rows and a row-count table in the test
- [ ] Schema describers (SQLite pragma-based; SQL Server `INFORMATION_SCHEMA` + `sys.*` with name normalisation) as shared test helpers
- [ ] `SqlServerFactAttribute`; `SqlServer` trait; CI job
- [ ] Core.Tests reference to Mcp **or** move the open-mode ladder into Workspace (recommended, Pattern 6) so `AdsWorkspace` stays a thin consumer
- [ ] Update: `SourceGuardTests` (forbid `EnsureCreated` outside bootstrapper's Sqlite/InMemory branch; add: `Migrate(` only in bootstrapper/Desktop/Workspace startup), `DatabaseBootstrapperTests` (Pitfall 6)
- [ ] **Human checkpoints:** (1) launch the built Mac Catalyst `.app` against copies of L2 and a corrupt file (A1); (2) restore drill from the Desktop drawer; (3) read the runbook once end-to-end

## Security Domain

`security_enforcement` is enabled (absent = enabled); ASVS level 1 per `.planning/config.json`.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no (no new auth surface) | - |
| V3 Session Management | no | - |
| V4 Access Control | partly: backup/quarantine/staging folders and `deploy.db` backups inherit the user profile ACL | create the `backups/` tree with default user-only profile permissions; on macOS set `UserRead\|UserWrite` on backup files (`File.SetUnixFileMode`); never place backups in a shared/temp directory |
| V5 Input Validation | yes | bound parameters for `VACUUM INTO`; picked restore files are untrusted: verify as a read-only connection with `trusted_schema=OFF`, schema allow-list (no triggers/views, known tables only), known `__EFMigrationsHistory` ids, size sanity, SHA-256 vs manifest when present; never build SQL from file names (the retention parser only matches `^workspace-\d{8}T\d{6}Z-(manual\|scheduled\|premigration)\.db$`) |
| V6 Cryptography | yes (integrity only) | BCL `SHA256`; no custom crypto; backups are plaintext like the live DB (OS profile protection) - state this in the runbook |
| V8 Data Protection | yes | backups/quarantine contain tenant inventory and designs; `deploy.db` backups may contain `AzureSubscriptions.ClientSecret`; keep them out of the user-facing list, exclude from any future support bundle (DIAG-03), never log row content (log counts and paths) |
| V12 Files and Resources | yes | restore staging directory, path canonicalisation (`Path.GetFullPath`) and containment checks for retention deletes; quarantine never deletes; temp-then-rename for backups |
| V14 Configuration | yes | `Database:Migrate`/`Database:AllowCreate` are fail-closed in production; CI SQL Server password is an ephemeral throwaway, not a secret; runtime DDL rights on the app login are an accepted risk until a bundle flow exists (documented) |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| SQL injection through a backup/restore path | Tampering | bound parameter for `VACUUM INTO` (verified with an apostrophe path); no concatenated paths in `ATTACH`/`VACUUM` |
| Maliciously crafted `.db` picked for restore (triggers, views, crafted pages) | Tampering / Elevation | `trusted_schema=OFF`, `integrity_check`, object allow-list, migrate-forward through EF only, keep the pre-restore file |
| Stale `-wal` replayed onto a restored file | Tampering (data integrity) | delete `-wal`/`-shm` during the swap; test it |
| Silent data loss: auto-recreate of a "missing" database | Denial of service / Repudiation | `AllowCreate` guard on SQL Server; quarantine (never delete) on SQLite; manifest + notice |
| Dedupe deletes rows | Tampering | backup first; removed rows written to a `.jsonl` report; only for guarded unique-index keys |
| Information disclosure through backups/quarantine files | Information disclosure | profile ACL, 0600 on macOS, not in shared folders, runbook states plaintext-at-rest |
| Abandoned migration lock = availability loss | Denial of service | bounded migrate + documented recovery; no silent lock deletion |
| Over-privileged runtime login performing DDL | Elevation | accepted for now (single replica); bundle + separate identity before scaling out |

## Sources

### Primary (HIGH confidence)
- Local reproductions (this session): EF Core 10.0.12 / Microsoft.Data.Sqlite 10.0.12 / SQLite 3.53.3 / SQL Server 2022 container / dotnet-ef 10.0.13 on SDK 10.0.403 - scratch projects and a scratch git worktree (removed); findings F1-F14.
- `src/` files read this session (paths and line ranges quoted above); `git show` of commits 2b33750, 0de69e6, d05566a, 013bdf9, 5536611, d23531e, 0021727, d9659ce.
- https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying - runtime `Migrate()`, migration locking (EF 9+), pending-model-changes error, EnsureCreated warning, bundles, separate deployment identity
- https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations - rebuild rules, no idempotent scripts, `__EFMigrationsLock` abandoned-lock recovery
- https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/providers - multiple providers, derived contexts, `MigrationsAssembly`
- https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings, .../transactions, .../database-errors - keywords, shared-cache + WAL caution, deferred transactions, automatic busy retry
- https://www.sqlite.org/wal.html, https://www.sqlite.org/lang_vacuum.html, https://www.sqlite.org/pragma.html - read-only WAL rules, persistence of WAL mode, checkpoint starvation, `VACUUM INTO` semantics, `quick_check`, `synchronous`, `query_only`, `journal_size_limit`, `wal_checkpoint`
- https://learn.microsoft.com/en-us/dotnet/maui/platform-integration/storage/file-picker - FilePicker platforms and options

### Secondary (MEDIUM confidence)
- nuget.org flat-container and search API (versions, owners, downloads) for Design, dotnet-ef, CommunityToolkit.Maui, Testcontainers.MsSql
- `.planning/research/{STACK,PITFALLS,ARCHITECTURE}.md` (project's earlier research; C2 corrects one claim in it)

### Tertiary (LOW confidence)
- Mac Catalyst runtime behaviour of `Migrate()` (A1), SQL Server `Migrate()` creating a missing database (A7), dynamic default-constraint drop (A8), `sqlcmd` path in the image (A9)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - versions resolved on nuget.org, restore/audit/lock impact measured in a scratch worktree.
- Architecture: HIGH for SQLite and SQL Server mechanics (reproduced); MEDIUM for Desktop wiring and UI seam (not built).
- Pitfalls: HIGH - each pitfall has a reproduced failure or a documented source; Pitfalls 8-10 are MEDIUM (platform/environment-dependent).

**Research date:** 2026-11-14
**Valid until:** 2026-12-14 for tool/package versions (EF 10.0.x patch releases land monthly - re-check the Design/tool pins at planning time); the verified behaviours are stable across 10.0.x.

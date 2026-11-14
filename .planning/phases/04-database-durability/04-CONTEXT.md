# Phase 4: Database Durability - Context

**Gathered:** 2026-11-14
**Status:** Ready for planning

<domain>
## Phase Boundary

Both databases get one schema authority: existing installations upgrade without losing a row, SQLite survives concurrent read/write, corruption is visible, and a user can restore a real backup. Requirements DATA-01..DATA-06, DATA-08. Depends on Phase 3 (server host harness and `Persistence/DatabaseBootstrapper` exist).

</domain>

<decisions>
## Implementation Decisions

### Backup and restore
- Desktop offers both a manual Backup and Restore command (file picker, `VACUUM INTO`, restore verifies the file with `quick_check` and row counts before swapping it in) and automatic backups in the app data folder (taken before every migration and on a schedule, with a bounded retention) - **Reversibility:** costly - the backup file format and the data-folder layout become something users rely on to restore

### Corruption at startup
- When `quick_check` fails at startup the Desktop app moves the corrupted file aside (quarantine, never deleted), creates an empty workspace, and shows a notice naming the quarantine path and the available backups so the user can restore - **Reversibility:** costly - a fresh workspace is created without asking; mitigated because the quarantined file and the automatic backups remain on disk

### Unique-index violations during upgrade
- Back up first, then keep the most recently updated row per key, delete the rest, and write the removed rows to a report file so fresh and upgraded databases end with the same schema - **Reversibility:** one-way - rows are deleted from the live database; undo requires the pre-upgrade backup

### Server (SQL Server) rollback
- The supported rollback is restoring the pre-upgrade backup, documented as a runbook and rehearsed in the SQL Server container CI job; EF `Down()` methods are generated but are not the supported path - **Reversibility:** costly - defines the operational procedure operators will follow

### Locked by the roadmap implementation note
- Baseline-as-guarded-SQL: migration 1 is the existing guarded DDL via `migrationBuilder.Sql` for both providers (legacy databases get a history row without re-creation); migrations 2+ are generated diffs - **Reversibility:** one-way - the first migration is the history every installation carries
- `dotnet-ef` 10.0.13, no `Cache=Shared`, WAL with `busy_timeout`, `foreign_keys` and per-connection pragmas set by a `WorkspaceDb` options/pragma factory; MCP opens WAL workspaces read-only (or reports the explicit `ReadWrite + query_only` fallback)
- Checked-in legacy fixtures built from today's bootstrap DDL (workspace SQLite and the SQL Server legacy schema); SQL Server parity runs in its own container job; back up before migrating, never re-create a user database
- Document `__EFMigrationsLock` recovery

### Decided autonomously
- [Backups] Q: "Where do automatic backups live and how many are kept" -> Selected: "a `backups` subfolder of the app data root, newest 5 kept plus every pre-migration backup of the last 30 days" - retention is a constant that can be changed without touching stored data
- [Backups] Q: "Backup file naming" -> Selected: "`workspace-<UTC yyyyMMddTHHmmssZ>-<reason>.db` where reason is manual, scheduled or premigration" - naming is internal and parsed only by the retention code
- [Quarantine] Q: "Quarantined file naming" -> Selected: "`workspace.corrupt-<UTC timestamp>.db` beside the data folder's workspace" - cheap to rename
- [Migrations] Q: "Where do migration classes live" -> Selected: "in `AzureDesignStudio.Workspace` for the workspace and in the Services project for the deploy/server context, with design-time factories beside them" - moving files later is a local refactor
- [Testing] Q: "How are concurrency and corruption tested" -> Selected: "xUnit tests on temp SQLite files (writer plus reader tasks, a deliberately truncated file) in Core.Tests, SQL Server parity in a separate container job" - test approach is local to the test project

</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets
- `Server/Persistence/DatabaseBootstrapper.cs` (Phase 3): single `EnsureCreated` site and the legacy SQL Server DDL gated by `IsSqlServer()`; `DatabaseProvider` selects SqlServer/Sqlite/InMemory by configuration; `DesignDbContextFactory` is the design-time factory pattern.
- `AzureDesignStudio.Workspace/WorkspaceDbContext.cs` (guarded `CREATE TABLE IF NOT EXISTS` DDL, `EnsureCreated` at line ~97); Desktop `MauiProgram.cs` ~line 210 (`deploy.db` `EnsureCreated`); `Mcp/AdsWorkspace.cs` opens the workspace read-only.
- `Server.Tests` harness (`AdsServerFactory`, temp SQLite per host) and `Core.Tests` for Workspace/Discovery.

### Established Patterns
- Central package versions in `Directory.Packages.props`; lock files checked in locked mode in CI; SDK 10.0.403 locally vs 10.0.401 pin rewrites unrelated lock files - revert drift.
- AGENTS.md: Desktop storage under `FileSystem.AppDataDirectory` (Mac: `MacHost.AppDataDirectory`); designs are local.

### Integration Points
- Workspace and deploy contexts in Desktop startup, MCP workspace open, Server `DatabaseBootstrapper`, CI workflows (new SQL Server container job).

</code_context>

<specifics>
## Specific Ideas

User chose, for backup UX, both manual and automatic backups; for corruption, automatic quarantine plus a fresh workspace; for duplicates, backup then dedupe keep-newest with a report; for the server, backup-restore rollback rehearsed in CI.

</specifics>

<deferred>
## Deferred Ideas

None - discussion stayed within phase scope.

</deferred>

---
phase: "04"
slug: "database-durability"
status: draft
nyquist_compliant: false
wave_0_complete: false
created: "2026-11-14"
---

# Phase 04 - Validation Strategy

> Per-phase validation contract for feedback sampling during execution. The test map, sampling rate and Wave 0 gaps below are lifted verbatim from 04-RESEARCH.md `## Validation Architecture`; plans must map their tasks onto the rows.

## Sampling Rate

- **After every task commit:** the quick run command (temp SQLite files, no network, under 30 s)
- **After every plan wave:** full Core.Tests offline suite plus Server.Tests (Release, `-p:RunAOTCompilation=false`)
- **Before `/gsd-verify-work`:** both suites green, the SQL Server container CI job green, and the human checkpoints done
- **Max feedback latency:** 120 seconds

## Validation Architecture (from 04-RESEARCH.md)

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


## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Mac Catalyst migrates an old workspace at startup | DATA-01 | needs the built .app | launch the built .app against copies of the L2 fixture and a corrupt file |
| Restore drill from the Desktop drawer | DATA-05 | UI flow | take a backup, restore it, verify data |
| Runbook read end to end | DATA-02 | documentation | follow docs/database-operations.md once |

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending

---
phase: 04-database-durability
reviewed: 2026-11-14T00:00:00Z
depth: standard
files_reviewed: 41
findings:
  critical: 0
  warning: 8
  info: 5
  total: 13
status: issues_found
---

# Phase 04: Code Review Report

**Reviewed:** 2026-11-14
**Depth:** standard
**Status:** issues_found

## Summary

All production code in scope was read; test files were skimmed for vacuous assertions (none found). The quarantine, restore, prune and dedupe paths were traced for ordering and failure-midway behaviour.

What holds up: nothing deletes or overwrites a user database without a verified copy, except the dedupe described in WR-05/WR-06 (covered by backup plus report); `VACUUM INTO` uses a bound parameter; the restore candidate is verified on a staged copy; `FolderReveal` passes one argument with no shell; the backup naming regex is anchored; the SQL Server baseline guards are sound. No hardcoded secrets; the CI SQL Server password is an ephemeral test-only credential.

The warnings are data-loss or availability risks: a healthy workspace can be quarantined and retention can then evict the good backups (WR-01, WR-02); several startup exceptions bypass the failure page (WR-03); "Restore from file" can restore a stale copy of the live database (WR-04); the dedupe can keep the wrong row and removes rows without telling the user at launch (WR-05, WR-06); two backup-creation robustness gaps (WR-07, WR-08).

## Warnings

### WR-01: Integrity classification treats every unlisted SQLite error as corruption, so a healthy workspace can be quarantined

**File:** `src/AzureDesignStudio.Workspace/Integrity/WorkspaceIntegrity.cs:103-109, 118-123`
**Issue:** `IntegrityCodes.IsInconclusive` is a deny-list of 8 environmental codes (3, 5, 6, 7, 8, 10, 13, 14). Every other `SqliteException` is classed `Corrupt`, including codes that say nothing about file content: `SQLITE_PROTOCOL` (15, WAL lock contention with another process), `SQLITE_INTERRUPT` (9), `SQLITE_ABORT` (4), `SQLITE_MISUSE` (21), `SQLITE_AUTH` (23), `SQLITE_SCHEMA` (17), `SQLITE_ERROR` (1). `WorkspaceStartup.OpenAsync` then calls `Quarantine`, moves the healthy `workspace.db` aside and creates an empty one. The class comment promises a second process holding the file must never cause a healthy workspace to be quarantined; the deny-list does not deliver that.
**Fix:** Invert to an allow-list. Only `SQLITE_CORRUPT` (11), `SQLITE_NOTADB` (26) and `SQLITE_FORMAT` (24) are `Corrupt`. Everything else is `Inconclusive`, so startup throws the "nothing was moved" failure instead of moving data (`IsCorrupt(primaryCode) => primaryCode is 11 or 24 or 26`).

### WR-02: Retention can evict the only good backups after a quarantine leaves an empty workspace

**File:** `src/AzureDesignStudio.Workspace/Backup/BackupRetention.cs:24-46`, `src/AzureDesignStudio.Workspace/Backup/WorkspaceBackupService.cs:47-62`
**Issue:** After a recovery a fresh empty workspace runs; the scheduler backs it up every 24 h as `scheduled` and each run prunes to "newest 5 scheduled and premigration, plus premigration within 30 days". Five days after the quarantine all pre-corruption `scheduled` backups are deleted, replaced by backups of an empty database; only premigration backups survive, for 30 days. A user who reads the "Workspace recovered" alert late loses the only restore points. The same applies to any wipe not noticed within 5 days.
**Fix:** Do not let a backup of a near-empty database displace non-empty ones: skip the scheduled backup and prune while the quarantine notice is unacknowledged, or exempt the newest N backups whose manifest `tableCounts` show data from the prune, or add thinning (weekly keepers).

### WR-03: Startup exceptions other than the two caught types crash the app instead of showing the failure page

**File:** `src/AzureDesignStudio.Desktop/MauiProgram.cs:112, 297`; `src/AzureDesignStudio.Workspace/Backup/RestoreStaging.cs:363, 414`
**Issue:** Only `WorkspaceUpgradeException` (line 112) and `InvalidOperationException` (line 297) route to `StartupFailurePage`. These escape `CreateMauiApp` as a raw crash: `RestoreStaging.ApplyPendingAsync` (`File.OpenRead(candidate)` can throw `IOException`/`UnauthorizedAccessException`; `Directory.CreateDirectory(directory)` in `WorkspaceStartup` can throw), and `DeployDbStartup.Migrate` (`SqliteException`/`DbUpdateException` from `GetPendingMigrations`, `AddMissingLegacyColumns` or `Migrate` is not an `InvalidOperationException`).
Restore ordering bug at `RestoreStaging.cs:413-414`: it runs `File.SetUnixFileMode(stem + ".db", ...)` when `moves.Count > 0`, after the candidate is already moved into place. If the live main file was absent but a `-wal` existed, the first move is the WAL, `stem.db` does not exist, and this throws `FileNotFoundException`; the restore has been applied, staging is not cleaned, and the exception is unhandled.
**Fix:** Catch `IOException`, `UnauthorizedAccessException`, `SqliteException` and `DbException` around both startup blocks and assign `StartupFailure`. In `ApplyPendingAsync` guard the mode call with `File.Exists(stem + ".db")`. Delete the staging folder in a `finally` once the candidate has moved.

### WR-04: "Restore from file" accepts the live workspace.db (or any file with a WAL sidecar) and restores a stale copy

**File:** `src/AzureDesignStudio.Desktop/Components/WorkspaceDataDrawerTemplate.razor:158-176`, `src/AzureDesignStudio.Workspace/Backup/RestoreStaging.cs:252-313`
**Issue:** The picker is unrestricted. If the user picks `workspace.db` from the data folder, `StageAsync` does `File.Copy(picked, candidate)`, copying only the main file: the live `-wal` is not copied so recent commits are missing, and the copy can be torn if a checkpoint is in flight. The copy still passes header, `integrity_check` and schema checks. At next launch the stale copy replaces the live workspace; the real one survives in `workspace.pre-restore-*` but nothing tells the user their latest work is missing.
**Fix:** In `StageAsync` reject a picked path that equals the workspace path, and reject any picked path with a non-empty `<picked>-wal` sidecar ("this database has unflushed changes; use Back up now and restore that backup").

### WR-05: Dedupe "keep newest" orders `DateTimeOffset` values by their text, so it can keep the older row

**File:** `src/AzureDesignStudio.Workspace/UniqueIndexGuard.cs:39-40, 129-131`
**Issue:** `SavedAt` and `ChangeTime` are `DateTimeOffset`, stored by EF Core SQLite as text such as `2024-03-31 01:30:00+01:00`. `KeepOrderBy = "SavedAt DESC, rowid DESC"` compares that text. Rows written under different UTC offsets (DST change, travel) do not sort chronologically as text, so the guard can keep the older design and delete the newer. The report and backup make it recoverable, but the default outcome is wrong.
**Fix:** Order by an instant, e.g. `julianday(SavedAt) DESC, rowid DESC` and `julianday(ChangeTime) DESC, rowid DESC`. Add a test with rows in two offsets.

### WR-06: Rows removed by the dedupe are committed before the migration and surfaced only in a drawer the user must open

**File:** `src/AzureDesignStudio.Workspace/WorkspaceSchema.cs:100-123`, `src/AzureDesignStudio.Desktop/MainPage.xaml.cs:36-50`
**Issue:** `Repair` commits its deletes on its own connection before `MigrateAsync`; if the migration then fails the duplicates stay deleted while the user sees the failure page. On success `MainPage.OnLoaded` alerts only for `Notice` and `Restore`; `UniqueIndexRepairs` is shown only as an Info alert inside the Workspace data drawer. Saved designs can disappear from a user's list with no notice at launch.
**Fix:** Add a launch alert in `OnLoaded` when `Report.Upgrade.UniqueIndexRepairs.Count > 0` giving the table, the count and the report path. Consider running the repair and migration in one scope so a failed migration does not keep the deletes.

### WR-07: Backup creation can overwrite an existing backup's manifest, and leaves orphan manifests that are never pruned

**File:** `src/AzureDesignStudio.Workspace/Backup/WorkspaceBackup.cs:45-92, 103-108`
**Issue:** The existence check at line 45 happens long before the moves. Two concurrent calls with the same stem, reason and second (a double-click on "Back up now") share the same `.tmp` path; the second call's `DeleteIfExists(tempPath)` removes the first call's in-flight output. The manifest is moved with `overwrite: true` at line 91 before the database is moved with `overwrite: false` at line 92; if the second move fails, the existing backup's manifest has been replaced by one with a different SHA-256 and a later restore of it is rejected as "SHA-256 does not match". A failure after line 91 leaves an orphan `.manifest.json`; `List` and `Prune` parse only `.db` names so orphans are never cleaned.
**Fix:** Move the database first (`overwrite: false`), then the manifest (a crash between leaves a database without a manifest, which restore tolerates with a note). Take a per-process `SemaphoreSlim` or a unique `.tmp` suffix (`Guid.NewGuid()`). Have `Prune` also remove manifests with no database.

### WR-08: The 30-second bound applies to the migration itself, not only to lock acquisition

**File:** `src/AzureDesignStudio.Workspace/WorkspaceSchema.cs:143-160, 188-193`
**Issue:** `MigrateAsync(cts.Token)` is cancelled after `MigrateTimeout` (30 s). For the guarded-DDL baseline that is fine, but a later data migration on a large `ResourceSnapshots` table that legitimately runs longer would be cancelled, and cancellation can leave `__EFMigrationsLock` held; every following start then times out the same way. The user is told to `DELETE FROM "__EFMigrationsLock"`, misleading for a slow-but-healthy migration and able to remove the lock of a migration still running.
**Fix:** Bound only lock acquisition, or scale the bound with database size; alternatively make the timeout a documented constant and reword `StuckUpgrade` to say a large database may need longer.

## Info

### IN-01: Backups are restricted to 0600/0700 but the live databases are not
**File:** `src/AzureDesignStudio.Workspace/Backup/WorkspaceBackup.cs:94-99`, `src/AzureDesignStudio.Desktop/MauiProgram.cs`
Quarantine, pre-restore and backup files are chmod'd user-only, but `workspace.db` and `deploy.db` (documented as holding client secrets) are created with the process umask (usually 0644) in a data folder created with default permissions; `-wal`/`-shm` of a quarantined or pre-restore database keep their original modes. Fix: create the data directory with `UnixFileMode` 0700, chmod live database files and moved sidecars.

### IN-02: Quarantined and pre-restore databases accumulate without bound
**File:** `src/AzureDesignStudio.Workspace/WorkspaceStartup.cs:112-152`, `src/AzureDesignStudio.Workspace/Backup/RestoreStaging.cs:379-382`
Each `workspace.corrupt-*.db` and `workspace.pre-restore-*.db` is a full plaintext copy, never listed, aged out or offered for deletion. Fix: list them in the Workspace data drawer with delete or keep-newest-N.

### IN-03: The new `sqlserver` CI job has no `permissions:` block
**File:** `.github/workflows/build.yml:119-174`
Inherits the default `GITHUB_TOKEN` scope. Fix: `permissions: { contents: read }` at job or workflow level.

### IN-04: The read-write `query_only` rung of the MCP reader can still write to the workspace folder
**File:** `src/AzureDesignStudio.Workspace/WorkspaceConnection.cs:143-153`, `src/AzureDesignStudio.Mcp/AdsWorkspace.cs`
`query_only` blocks statements but a read-write connection on a WAL database checkpoints and deletes `-wal`/`-shm` on last close, so "can never write" is not strictly true. Fix: soften wording to "never modifies content" or document the checkpoint-on-close behaviour for that rung.

### IN-05: `IRelationalDatabaseCreator.Exists()` can report a false "does not exist"
**File:** `src/AzureDesignStudio.Server/Persistence/DatabaseBootstrapper.cs:73-84`
SQL Server error 4060 is returned both for a missing database and for a login without access to an existing one. With `AllowCreate=false` the operator is told "does not exist, check for a typo"; with `AllowCreate=true` the server attempts `CREATE DATABASE` for a name that already exists. Fix: mention in the message and docs that a permissions problem looks the same.

---

_Reviewed: 2026-11-14_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_

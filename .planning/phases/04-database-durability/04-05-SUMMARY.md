---
phase: 04-database-durability
plan: 05
subsystem: database
tags: [sqlite, wal, vacuum-into, backup, retention, integrity-check, sha256]
status: complete

requires:
  - phase: 04-database-durability
    provides: 04-02 baseline migration (the __EFMigrationsHistory the manifest reads) and WorkspaceDb
provides:
  - WorkspaceIntegrity.Check (quick/full, live/snapshot) with Ok/Corrupt/Inconclusive/Missing outcomes, and internal IntegrityCodes.IsInconclusive
  - WorkspaceBackup.CreateAsync (VACUUM INTO, manifest, SHA-256, atomic placement), List, PruneAsync; generic over the file stem
  - BackupNaming (strict anchored name format), BackupReason, BackupFile, BackupManifest (sidecar <backup>.db.manifest.json)
  - BackupRetention.Plan as one pure function, RetentionPolicy.Default (5, 30 days), RetentionPlan
affects: [04-07, 04-08, 04-09, 04-11, 04-12]

plan_head_before: 763a807ab897391afd5361a6ec6325b9b55a65c2
plan_head_after: f45fca5aa3dac18a2d04b42809314d789084400f

actuals:
  tokens: 13700   # chars/4 over the 9-file diff (git diff 763a807..HEAD, 54,911 chars)
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "Backup = VACUUM INTO a bound-parameter path to <final>.tmp, snapshot quick_check, hash, manifest via temp name, then File.Move without overwrite"
    - "Integrity outcome is classified by SQLite primary code: environmental codes (3,5,6,7,8,10,13,14) are Inconclusive, never Corrupt"
    - "Retention is a pure function over descriptors; the file system is touched only by PruneAsync, behind a full-path containment check"

key-files:
  created:
    - src/AzureDesignStudio.Workspace/Integrity/WorkspaceIntegrity.cs
    - src/AzureDesignStudio.Workspace/Backup/BackupNaming.cs
    - src/AzureDesignStudio.Workspace/Backup/BackupManifest.cs
    - src/AzureDesignStudio.Workspace/Backup/BackupRetention.cs
    - src/AzureDesignStudio.Workspace/Backup/WorkspaceBackup.cs
    - src/AzureDesignStudio.Core.Tests/WorkspaceBackupTests.cs
    - src/AzureDesignStudio.Core.Tests/BackupRetentionTests.cs
    - src/AzureDesignStudio.Core.Tests/WorkspaceIntegrityTests.cs
  modified:
    - .gitignore

key-decisions:
  - "The free-space probe is an optional parameter of an internal CreateAsync overload, not a mutable static holder, so a test lowering it cannot affect tests running in parallel (04-07 and later plans call CreateAsync from other test classes)"
  - "PruneAsync returns the deleted BackupFile list as the plan says; files it skips or cannot delete are reported through a trailing optional onProblem callback so the return type and the leading positional parameters stay as planned"
  - "LiveFile integrity checks open ReadWrite (never creating) so a leftover WAL recovers; Snapshot checks open ReadOnly. A read-only open of a WAL-mode file can leave an empty -wal/-shm beside it, which is why backups (delete mode) use Snapshot and the live file uses LiveFile"
  - "Backup names are matched with [0-9] and \\z rather than \\d and $ (the former admit non-ASCII digits and a trailing newline)"
  - "Manifest sidecar is <backup>.db.manifest.json (BackupManifest.PathFor), which is the name plan 04-09 expects"

patterns-established:
  - "Backup/ folder in the Workspace project is un-ignored in .gitignore (the Visual Studio template ignores Backup*/)"

requirements-completed: []  # DATA-04 and DATA-05 are closed by the orchestrator at phase completion

duration: 40min
completed: 2026-11-14
---

# Phase 04 Plan 05: WAL-safe VACUUM INTO backup service and an integrity check that tells corruption from contention Summary

**`VACUUM INTO` backups of a live WAL workspace (uncheckpointed rows included) are verified, SHA-256-hashed, manifested and placed atomically under a strict name; retention is one pure function; and the integrity check classifies lock, permission and IO errors as inconclusive so a healthy workspace can never be called corrupt.**

## Accomplishments

- `WorkspaceIntegrity.Check(path, depth, access)`: missing file is `Missing`; a zero-byte file with a non-empty `-wal` is `Corrupt` ("zero-length database with a write-ahead log"), without one a valid empty database is `Ok`; `PRAGMA quick_check`/`integrity_check` returning anything but the single row `ok` is `Corrupt`; `SqliteException` codes 3/5/6/7/8/10/13/14 and `IOException`/`UnauthorizedAccessException` are `Inconclusive`, every other code (11, 26, unknown) is `Corrupt`. The class summary records that this narrows the CONTEXT "any exception is corruption" entry and states the `quick_check` blind spot.
- `WorkspaceBackup.CreateAsync`: refuses a missing source (`FileNotFoundException`) and an existing target (`IOException`) before any work; requires 1.2 x the source size free; creates the directory 0700 and the files 0600 on Unix; `VACUUM INTO $path` with a bound parameter to `<final>.tmp`; snapshot `quick_check`; per-table row counts, latest migration id, SHA-256 and size into the manifest (written through a temp name); renames the manifest then the database (no overwrite); deletes the temp files if anything fails.
- `BackupNaming`: `<stem>-<yyyyMMddTHHmmssZ>-<manual|scheduled|premigration>.db`; `TryParse` accepts only the anchored pattern, so `.tmp` files, manifests, other stems, path separators, case variants, trailing newlines and non-ASCII digits never match; `List` ignores everything else and a missing directory.
- `BackupRetention.Plan`: keeps every manual backup, the newest 5 of scheduled and premigration (time descending, then file name ordinal), and every premigration backup at most 30 days old; no I/O. `WorkspaceBackup.PruneAsync` applies it, checks `GetFullPath` containment before every delete, removes the `.db` and its manifest, and tolerates a file that is in use.
- `WorkspaceIntegrityTests`: truncation to a non-page multiple, zeroed 100-byte header (both depths and snapshot), text file, zero-byte file with and without a WAL (and with an empty one), missing path, an unreadable file (`chmod 000`: `Inconclusive`, then `Ok` once readable again), the code table.

## Task Commits

1. **Task 1 (tracer): WAL-safe VACUUM INTO backup with manifest, and an integrity check** - `2a0de97` (feat)
2. **Task 2: backup retention as a pure plan with contained pruning** - `eecedaa` (feat)
3. **Task 3: integrity check matrix and contention classification** - `f45fca5` (test)

## Verification

- Tracer gate: `WorkspaceBackupTests` 7 passed (rows that exist only in the live `-wal` are in the backup; backup `journal_mode` is `delete`; snapshot `quick_check` Ok; manifest counts equal live counts; manifest SHA-256 equals an independently recomputed hash; no `.tmp` left; directory name with an apostrophe works; second create with the same timestamp and reason throws `IOException` and leaves the first byte-identical; free-space shortfall refused; Unix modes 0700/0600).
- `BackupRetentionTests` + `WorkspaceBackupTests`: 34 passed (27 retention/naming/prune: manual always kept, newest 5, 30-day edge kept and one second past it dropped, deterministic ties, strict-name theory with 12 hostile names, contained prune with an unrelated file, another stem and a sibling directory untouched, a read-only directory reports instead of throwing).
- `WorkspaceIntegrityTests`: 21 passed.
- Full offline suite `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"`: 1717 passed, 0 failed, 0 skipped.
- Acceptance greps: `VACUUM INTO` in WorkspaceBackup.cs 2; bound parameter present (`Parameters.AddWithValue`); `File.Copy` 0; `File.`/`Directory.` in BackupRetention.cs 0; `GetFullPath` in WorkspaceBackup.cs 5; `IsInconclusive` and `write-ahead` present in the integrity tests.
- Lock files: no `packages.lock.json` was modified by the test runs (`git status` clean apart from the plan's own files).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `.gitignore` ignored the planned `Backup/` source folder**
- **Found during:** Task 1 (`git status` did not list `src/AzureDesignStudio.Workspace/Backup/`)
- **Issue:** the Visual Studio template rule `Backup*/` (line 251) matches the folder the plan names, so the new source files could never have been committed.
- **Fix:** added `!/src/AzureDesignStudio.Workspace/Backup/` right after that rule, with a comment.
- **Files modified:** `.gitignore`
- **Commit:** `2a0de97`

**2. [Shape choice] free-space probe as a parameter, `PruneAsync` problem callback**
- The plan asks for an `internal Func<string,long>` probe "field on a static options holder". A mutable static would let one test change the behaviour of `CreateAsync` for test classes running in parallel (plans 04-07/04-09 call it), so the probe is an optional parameter of an internal `CreateAsync` overload; the public signature is as planned.
- `PruneAsync` returns the deleted `BackupFile`s as planned; "skip and report" is an optional trailing `Action<string>? onProblem` (after `ct`), so existing positional calls are unaffected.

## Not verified here

- The guard mutations named in the acceptance criteria (implement the backup as a file copy; read the file system inside `Plan`) were not run as mutations; the counts were checked on the final code only.
- A real busy/locked classification (a second process holding an exclusive lock) is not exercised: Microsoft.Data.Sqlite's 30 s default busy timeout would make that test slow. The unreadable-file test (primary code 14) and the pinned code table cover the rule instead.
- Windows ACLs for the backups directory ("profile ACL on Windows") are not set or tested; on Windows the folder inherits the ACL of its parent, which is the user profile under `AppDataDirectory`. The Unix modes were verified on macOS only.
- Nothing is wired into `WorkspaceSchema`, `WorkspaceStartup` or Desktop (prohibited by the plan); the helpers are consumed by plans 04-07, 04-08, 04-09 and 04-11.

## Known Stubs

None.

## Threat Flags

None. T-04-14 (bound path, apostrophe test), T-04-15 (0700/0600; plaintext-at-rest stays for the runbook in 04-12), T-04-16 (anchored name, containment check, manual never pruned), T-04-17 (contention codes Inconclusive; live check opens read-write) and T-04-18 (temp name, snapshot check, hash, no-overwrite rename) are implemented.

## Self-Check: PASSED

- FOUND: WorkspaceIntegrity.cs, BackupNaming.cs, BackupManifest.cs, BackupRetention.cs, WorkspaceBackup.cs, WorkspaceBackupTests.cs, BackupRetentionTests.cs, WorkspaceIntegrityTests.cs
- FOUND commits: 2a0de97, eecedaa, f45fca5

# Database operations runbook

This is the operator's guide to the three databases Azure Design Studio owns, what the app does to them at startup, and how to recover from each failure that startup, upgrade, backup and restore can produce.

| Database | Used by | Engine | Schema owner |
|----------|---------|--------|--------------|
| `workspace.db` | Desktop app (designs, scans, snapshots, governance findings, cost cache, plans, audit); read by `ads-mcp` | SQLite | EF migrations in `src/AzureDesignStudio.Workspace/Migrations` |
| `deploy.db` | Desktop app (approvals, guardrails, linked subscriptions, audit) | SQLite | EF migrations in `src/AzureDesignStudio.Services/Migrations/Sqlite` |
| `ads-main` | Server (designs, links, approvals) | SQL Server (production) | EF migrations in `src/AzureDesignStudio.Server/Persistence/Migrations` |

Everything below that touches a database file with the app running is wrong: close the app, and any `ads-mcp` process, before you move, copy or edit a database file.

All database files, backups, quarantined files and reports described here are stored as **plaintext at rest**. The app does not encrypt them; they are protected only by the permissions of your user account (the app creates its backup and staging folders `0700` and the files in them `0600` on macOS). Treat every copy as sensitive. In particular, `deploy.db` and everything under `backups/deploy/` contain the client secrets of linked service principals: never share them, never attach them to a bug report, and delete copies you no longer need.

## Where the data lives

The desktop app keeps everything in one data folder. The "Workspace data" menu item opens a drawer that prints the exact folder and the backups folder and has an "Open backups folder" button; use it if you are unsure which folder your installation uses.

| What | Name | Location |
|------|------|----------|
| Workspace database | `workspace.db`, plus `workspace.db-wal` and `workspace.db-shm` while the app runs | data folder |
| Deploy database | `deploy.db` (and its `-wal` / `-shm` if present) | data folder |
| Workspace backups | `workspace-<UTC>-<manual|scheduled|premigration>.db` and `<file>.manifest.json` | `backups/` |
| Removed-rows reports | `upgrade-removed-rows-<UTC>.jsonl` | `backups/` |
| Deploy database backups (private) | `deploy-<UTC>-premigration.db` | `backups/deploy/` |
| Staged restore | `candidate.db` and `restore.pending.json` | `restore-staging/` |
| Quarantined corrupt workspace | `workspace.corrupt-<UTC>.db` (and `-wal` / `-shm`) | data folder |
| Workspace kept by a restore | `workspace.pre-restore-<UTC>.db` (and `-wal` / `-shm`) | data folder |

Default data folder:

- **macOS:** `~/Library/Application Support/com.azuredesignstudio.desktop`. The app is not sandboxed, so this is the real folder in your home directory.
- **Windows:** the MAUI app-data folder of the installed app. Use the "Workspace data" drawer to read the path. `ads-mcp` assumes the unpackaged location `%LOCALAPPDATA%\Azure Design Studio\AzureDesignStudio.Desktop\Data`. The Windows Desktop target has not been exercised since the durability work was added (it could not be built or run on the machine that built it), so check the drawer path against your installation before relying on any Windows path in this document.

`ADS_DATA_DIR` overrides the data folder. When the environment variable is set, `workspace.db`, `deploy.db`, `backups/` and `restore-staging/` all live in that folder instead of the default. It exists so the built app can be started against a copy of a workspace, or a deliberately corrupted file, without touching your real data. Normal use needs nothing. `src/VerifyDesktopWorkspace.sh` (macOS) uses it to launch the app against a throwaway folder for the scenarios `l2`, `l3`, `l4` and `corrupt`.

`ads-mcp` has its own default path that is correct for an unpackaged Windows install only. On macOS, pass the file explicitly: `ads-mcp --db "$HOME/Library/Application Support/com.azuredesignstudio.desktop/workspace.db"` (or set `ADS_WORKSPACE_DB`). Its default path does not match the macOS data folder until a later phase aligns them.

## What happens at startup

The desktop app opens the workspace in this order. Each step runs before the next, and any failure stops the sequence with the file named in the message.

1. **Apply a staged restore**, if one is pending (see Restore). The current workspace is moved aside, never deleted.
2. **Integrity check** (`PRAGMA quick_check`) of the live file. A missing file or a valid empty database is fine.
   - Corrupt: the file is **quarantined** (see Corruption), and a new empty workspace is created. A native "Workspace recovered" alert tells you where the old file went and which backups exist.
   - Inconclusive (the file is locked by another process, a permissions or I/O problem, or any SQLite error other than the three that mean damaged content: `SQLITE_CORRUPT`, `SQLITE_FORMAT`, `SQLITE_NOTADB`): nothing is moved. The app stops with a message naming the file. Close any other copy of the app or `ads-mcp` and start again.
3. **Premigration backup.** If the file exists, is not empty and has migrations pending, a `premigration` backup is written to `backups/` first. If the backup cannot be written (no space, no permission), the upgrade is not attempted and the workspace file is unchanged.
4. **Unique-index dedupe.** Rows that would violate a unique index the new schema creates are removed, newest kept, and written to a report first (see Removed duplicate rows).
5. **Legacy column adoption.** A workspace that predates migrations gets the two scan columns the baseline cannot add.
6. **Migrate**, bounded to 30 seconds. SQLite schema changes are transactional, so a failed migration rolls back and the file is as it was.
7. **WAL assertion.** The journal mode must be `wal` afterwards; anything else stops the app.

A failed upgrade is **not** corruption. The workspace is not quarantined: the app shows the failure page, "Your workspace could not be opened", with the message, an "Open data folder" button and "Quit". The message names the premigration backup that was taken. Fix the cause (for a stuck lock see Migration lock recovery), then start the app again; the upgrade is retried from the beginning.

`deploy.db` is migrated right after the workspace (see deploy.db below for what is shown when that step fails).

## Backups

- **Premigration**: taken automatically before any schema upgrade of an existing file, as described above.
- **Scheduled**: taken while the app runs. At startup, and then every 24 hours, the app checks whether the newest scheduled or premigration backup is more than 24 hours old; if so it takes one, prunes, and then tries to truncate the write-ahead log. A manual backup does not count towards the 24 hours.
- **Manual**: the "Back up now" button in the "Workspace data" drawer.

Backups are written with `VACUUM INTO`, so they are consistent snapshots even while the app is running. A backup needs about 1.2 times the size of the workspace in free disk space, and is refused without it.

File name: `workspace-<yyyyMMddTHHmmssZ>-<manual|scheduled|premigration>.db`, always UTC, with a sidecar `workspace-<...>.db.manifest.json`. The manifest records `createdUtc`, `reason`, `appVersion`, `latestMigrationId`, `tableCounts` (a row count per table), `sha256`, `sizeBytes` and `sourceFileName`. Restore uses the manifest to confirm the file is the one that was written.

Retention, applied after each scheduled backup:

- every **manual** backup is kept forever (never pruned);
- of the scheduled and premigration backups, the **newest five** are kept;
- every **premigration** backup from the **last 30 days** is kept as well;
- the **newest two** scheduled or premigration backups whose manifest `tableCounts` show data (any table other than the migration bookkeeping tables has rows) are kept as well. After a quarantine or an unnoticed wipe the app runs on an empty workspace and backs that up every day; without this rule five days of backups of an empty database would push every backup that holds your data out of the newest five. A backup with a missing or unreadable manifest counts as holding data. These two extra files stay until two newer backups with data exist, so after you deliberately empty a workspace they are not pruned; delete them yourself when you no longer need them.

Everything else is deleted together with its manifest. A manifest whose backup file is gone is removed too. Only files whose names match the pattern above are ever touched, so files you put in `backups/` yourself are left alone. `deploy.db` premigration backups in `backups/deploy/` keep the newest three, with no age rule.

A SIGTERM (or a crash) during a scheduled backup can leave a file such as `workspace-<UTC>-scheduled.db.<8 hex digits>.tmp` in `backups/`. It is an unfinished copy of a backup that was never completed; it is safe to delete, and the app never reads it.

## Restore

Restore is two steps, never a hot swap: the app **stages** and verifies the file now, and **applies** it at the next launch, before any database connection exists. There is no hot restore.

From the app:

1. Open the "Workspace data" drawer.
2. Either press "Restore" on a listed backup, or press "Restore from file..." and choose a backup file.
3. The app copies the file into `restore-staging/` and verifies the **copy**: the file exists and is not empty, has the SQLite header, passes `PRAGMA integrity_check`, contains no triggers, views or virtual tables, contains only tables the workspace model knows, and records only migration ids this version of the app knows (a backup from a newer version is refused). If a manifest sits beside it, its SHA-256 and per-table row counts must match. A rejected file is not staged and the drawer lists every reason. The live `workspace.db` of the data folder, and any database with a non-empty `-wal` file beside it, are refused outright: copying only the main file would silently drop recent work. Use "Back up now" and restore that backup.
4. The drawer shows the pending restore with "Cancel pending restore" and "Quit now". Quit and start the app again.
5. At launch the staged file is hashed again, the current workspace (and its `-wal` and `-shm`) is moved aside as `workspace.pre-restore-<UTC>.db`, and the candidate takes its place. The usual startup steps then run on the restored file: integrity check, premigration backup if the backup is older than this version, migration. A "Workspace restored" alert names the backup used. A restore that could not be applied shows "Restore not applied" with the reason and stays pending.

The previous workspace is never deleted by a restore. To undo a restore, restore from the `workspace.pre-restore-<UTC>.db` file the same way, or use the manual method below. Delete the `pre-restore` files yourself when you no longer need them.

If a restore fails because a file could not be moved, close `ads-mcp` and any other program that has the workspace open, and start the app again.

Manual alternative, with the app closed:

1. Move `workspace.db`, `workspace.db-wal` and `workspace.db-shm` (whichever exist) out of the data folder.
2. Copy the backup into the data folder and name it `workspace.db`.
3. Start the app. The startup steps run on the copied file.

Do not copy a backup over a live `workspace.db` without moving the old `-wal` and `-shm` aside first; a stale write-ahead log from the old database must never be replayed onto a different file.

## Corruption

A workspace that fails the startup integrity check is **quarantined**: `workspace.db`, `workspace.db-wal` and `workspace.db-shm` are moved to `workspace.corrupt-<UTC>.db` (and `-wal`, `-shm`) in the data folder, nothing is deleted, and a new empty workspace is created. The "Workspace recovered" alert names the quarantined file and lists the most recent backups. To get your data back, restore the newest backup (see Restore). You can also keep the quarantined file for forensics or send it to a developer; remember it is plaintext and holds your tenant inventory.

The startup check is a quick check, and it has a blind spot: `PRAGMA quick_check` does not verify that the content of an index matches the content of its table. A database with a damaged index can pass the startup check. Restore verification and the manual check below use the full `PRAGMA integrity_check` instead. To check a file by hand, with the app closed:

```bash
sqlite3 workspace.db 'PRAGMA integrity_check;'
```

The only acceptable answer is a single line, `ok`. Anything else means the file is damaged: restore the newest backup.

## Migration lock recovery

Symptom: startup fails after about 30 seconds with a message that says the workspace upgrade did not finish, that an earlier upgrade may have been interrupted, and that it left its lock in the table `__EFMigrationsLock`. This happens only when a previous upgrade was killed while it held the migration lock.

The app never deletes the lock automatically: a lock can also mean another process is genuinely migrating, and removing it from under that process would corrupt the upgrade. Recover by hand:

1. Close the app, and any `ads-mcp` process. Make sure no other copy of the app is running.
2. Run this statement against the database file named in the error message:

   ```bash
   sqlite3 workspace.db 'DELETE FROM "__EFMigrationsLock";'
   ```

3. Start the app again. A premigration backup was taken before the stuck attempt; the workspace data is unchanged.

The statement is printed in the exception message (`src/AzureDesignStudio.Workspace/WorkspaceSchema.cs`) in exactly this form. `deploy.db` uses the same lock table, but its migration is not time-bounded: a stuck lock there shows as a launch that never finishes rather than as this message. The same statement applies, run with `sqlite3 deploy.db`.

## Removed duplicate rows

The first migration adds unique indexes (design names, change-feed keys). A workspace that already holds rows that would violate them could not take the index, so before migrating, the app removes the duplicates: for each key it keeps the most recently updated row and deletes the others, all in one transaction, and only after the premigration backup exists.

When rows were removed, the next launch shows a "Duplicate rows removed" alert naming the table, the number of rows, the report and the premigration backup; the Workspace data drawer lists the same repairs. The cleanup is committed before the schema migration runs and is not rolled back if that migration then fails: the failure page says so, and the report and the premigration backup still hold every removed row.

Every removed row is written first to `upgrade-removed-rows-<UTC>.jsonl` in `backups/`, one JSON line per removed row with the table, the index, the key columns, the kept and removed `rowid` and the whole removed row. The file is plaintext and may hold design content, so handle it like a backup.

To recover a removed row, look it up in the report (the `row` field), or open the premigration backup that was taken just before the upgrade (its name has the same UTC second) read-only with `sqlite3` and select the row by its `rowid`; the premigration backup still holds all the duplicates. A workspace without duplicates produces no report.

## MCP read-only modes

`ads-mcp` never writes the workspace. It opens it through a ladder and reports the rung on stderr when it starts, for example `ads-mcp: workspace opened ReadOnly: ...` followed by the reason.

| Mode | Meaning |
|------|---------|
| `ReadOnly` | The normal case. The database opened with a read-only connection and sees the desktop app's live write-ahead log. |
| `ReadWriteQueryOnly` | The platform would not open the write-ahead-log index read-only, so the file is opened read-write with `query_only` set. Still read-only in effect: every write is rejected. |
| `Immutable` | No locking at all, used only for a read-only folder with no live write-ahead log. The data can be stale if a writer starts later. |

A file that is corrupt, not a database, or locked at open time fails at once and `ads-mcp` exits with code 2 and `cannot open workspace`.

`ads-mcp --db <path> --self-check 5` replaces the MCP host with a bounded read loop that lasts the given number of seconds (1 to 600) and prints `self-check mode=... reads=... errors=...` on stderr. It is the way to prove that a reader in its own process keeps reading while the desktop app writes. Exit codes: 0 no read failed, 2 the workspace could not be opened or an argument was wrong, 3 at least one read failed.

## Concurrency and WAL

- The workspace runs in write-ahead-log (WAL) mode, asserted at every startup.
- Every connection waits up to 5000 ms for a lock (`busy_timeout`) before reporting a busy database. The shared-cache mode is not used.
- Do not place the data folder on a network share or a cloud-sync folder: WAL needs shared memory that network file systems do not provide, and a sync client can copy a half-written file.
- A long-running reader (an `ads-mcp` process in the middle of a big query, a `sqlite3` shell left open) prevents the WAL from being truncated, so `workspace.db-wal` can grow until it finishes. The writer limits the WAL file to 64 MiB after a checkpoint where it can, and the scheduled backup tries `wal_checkpoint(TRUNCATE)`. Close idle shells and the file shrinks again.
- `workspace.db` is a single-writer file. Run one copy of the desktop app against a data folder at a time.

## deploy.db

`deploy.db` holds deploy approvals, guardrails, linked subscriptions (including service principal client secrets) and the audit trail. It is migrated at launch with EF migrations, like the workspace. An existing `deploy.db` that needs an upgrade gets a premigration backup first in `backups/deploy/`, kept private (the folder is `0700`, the files `0600` on macOS). **These backups contain subscription client secrets in plaintext.** Never share them, never attach them to a bug report, never put them in a synced folder. If the premigration backup of `deploy.db` cannot be written, the upgrade is not attempted, `deploy.db` is unchanged and the app shows the same failure page as for the workspace. A `deploy.db` migration that itself fails (a database, file or permission error) shows the same page, and the crash log `ads_desktop_crash.log` in the system temp folder has the details; SQLite schema changes are transactional, so the file is as it was. To go back to a previous state, close the app, move `deploy.db` (and its `-wal`, `-shm`) aside and copy the chosen file from `backups/deploy/` in as `deploy.db`.

## SQL Server (the server database)

The Server stores designs, subscription links and plan approvals in the database `ads-main`. Production uses SQL Server. `Sqlite` and `InMemory` exist for development and tests only and have no migrations.

Configuration keys:

| Key | Default | Meaning |
|-----|---------|---------|
| `Database:Provider` | `SqlServer` | `SqlServer`, `Sqlite` or `InMemory`. |
| connection string `ads-main` | none | From the Key Vault named by `KeyVaultName`, or from `ConnectionStrings__ads-main`. A standard SQL Server connection string; the examples below use `<database>` and `<path>` placeholders only. Never write the real value into a file in the repository or into this document. |
| `Database:Migrate` | `true` | When `true`, the host applies pending migrations at startup. When `false`, the host **refuses to start** while migrations are pending and prints the command to run, `dotnet ef database update --context SqlServerDesignDbContext --project src/AzureDesignStudio.Server`. It is the switch for a future migration-bundle flow. |
| `Database:AllowCreate` | `false` | When `false`, the host refuses to start if the database in the connection string does not exist, so a typo does not yield a silent empty database. Set it to `true` only for the very first deployment of a new environment (see First deployment), then remove it. |

A value other than `true` or `false` for either flag stops the host instead of being guessed.

**One replica.** Production runs a single replica (`replicas: 1` in `src/Configurations/main/deployment.yaml`). Keep it at one until a bundle flow with a separate deployment identity exists: with several replicas every starting pod would run the migrations and every pod's login would need DDL rights. **The application login needs DDL rights today** (the host migrates with the login it serves requests with). This is accepted for now because there is a single replica and migrations run once at startup; splitting the deployment identity from the runtime identity is the follow-up.

### First deployment of a new environment

1. Create or choose the SQL Server instance, and store the `ads-main` connection string in the secret store (never in the repository).
2. Set `Database:AllowCreate=true` for this one deployment. The host creates the database, applies every migration from the baseline, and writes the migration history.
3. Remove `Database:AllowCreate` (or set it to `false`) before the next deployment. Leaving it on means a mistyped database name creates a new empty database instead of failing.

### Upgrade procedure

1. **Take and verify a backup** (see Backup).
2. Deploy the new image. The host applies the pending migrations at startup.
3. Watch the startup log for `Applied <n> database migration(s): <ids>`. A host that stops with an exception about the database has changed nothing it could not roll back; read the message.

A database created before migrations existed is adopted by the baseline migration without recreating anything: its guarded statements skip what already exists and a history row is written.

### Backup

The repository does not record which hosting production `ads-main` uses (self-hosted SQL Server, or Azure SQL), so choose the variant that matches your hosting.

**Self-hosted SQL Server:**

```sql
BACKUP DATABASE [<database>] TO DISK = N'<path>/<database>-pre-upgrade.bak' WITH INIT, CHECKSUM, FORMAT;
RESTORE VERIFYONLY FROM DISK = N'<path>/<database>-pre-upgrade.bak' WITH CHECKSUM;
```

Do not proceed to the upgrade until `RESTORE VERIFYONLY` has succeeded.

**Azure SQL Database:** the service takes automatic backups that allow a point-in-time restore. Before an upgrade, note the current time (UTC) as the restore point, or make an explicit copy:

```sql
-- run in the master database of the logical server
CREATE DATABASE [<database>-pre-upgrade] AS COPY OF [<server>].[<database>];
```

Wait for the copy to finish before deploying.

### Rollback

The supported rollback is **restoring the pre-upgrade backup**. The `Down()` methods of the migrations are generated by EF but are not the supported path and are not exercised: they cannot restore data that an upgrade changed.

Self-hosted SQL Server:

1. Stop the app, or scale it to zero, so nothing holds the database.
2. Restore:

   ```sql
   ALTER DATABASE [<database>] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
   RESTORE DATABASE [<database>] FROM DISK = N'<path>/<database>-pre-upgrade.bak' WITH REPLACE, CHECKSUM;
   ALTER DATABASE [<database>] SET MULTI_USER;
   ```

3. Roll the image back to the previous version, then start it.

This is exactly the sequence the CI job rehearses: `BackupMigrateRestore_ReturnsToThePreUpgradeState` in `src/AzureDesignStudio.Server.Tests/Persistence/SqlServerMigrationTests.cs` takes a backup with `CHECKSUM`, verifies it, migrates, writes a row, restores with `REPLACE`, and asserts the pre-upgrade rows are back with no history table and that the database migrates again. The `sqlserver` job in `.github/workflows/build.yml` runs it on every build against a throwaway container. Locally, `src/RunSqlServerTests.sh` starts a SQL Server 2022 container, runs the `Category=SqlServer` tests and removes the container (it needs Docker; on Apple silicon the image runs under amd64 emulation and the first start takes minutes).

Azure SQL Database: the rollback is a **restore to a new database** (point-in-time restore to the time noted before the upgrade, or rename the `AS COPY OF` copy), then swap the connection secret `ads-main` to the new database and roll the image back. The in-place `RESTORE DATABASE ... WITH REPLACE` is not available on Azure SQL Database. The CI job rehearses the self-hosted sequence; the Azure SQL procedure follows the service's own documentation and has not been rehearsed against Azure.

### Migration lock on SQL Server

SQL Server migrations take an application lock that belongs to the connection, so it is released when the connection closes, including when the process dies. There is no table to clean up and no equivalent of the workspace `__EFMigrationsLock` recovery.

### Developer commands

Restore the pinned tool once per checkout:

```bash
dotnet tool restore
```

Create a migration for each context (run from the repository root; `<Name>` is a short PascalCase description):

```bash
# workspace.db
dotnet ef migrations add <Name> --context WorkspaceDbContext --project src/AzureDesignStudio.Workspace --startup-project src/AzureDesignStudio.Workspace --output-dir Migrations

# server SQL Server database
dotnet ef migrations add <Name> --context SqlServerDesignDbContext --project src/AzureDesignStudio.Server --startup-project src/AzureDesignStudio.Server --output-dir Persistence/Migrations

# deploy.db (the Services project holds the migrations, the Server hosts the design-time factory)
dotnet ef migrations add <Name> --context SqliteDesignDbContext --project src/AzureDesignStudio.Services --startup-project src/AzureDesignStudio.Server --output-dir Migrations/Sqlite
```

Rules: a baseline migration is **never edited** once released; every later migration is a generated diff. Pending-model-change tests (`HasPendingModelChanges`) turn a forgotten migration into a failing test, so a model change without a migration fails the build. On a machine whose .NET SDK differs from `global.json`, `dotnet ef` can rewrite unrelated `packages.lock.json` files; revert those and commit only the migration and its snapshot.

---
phase: 04-database-durability
plan: 11
subsystem: desktop-host
tags: [maui, mac-catalyst, blazor, workspace-startup, backup, restore, failure-page]
status: complete

requires:
  - phase: 04-database-durability
    provides: 04-07 WorkspaceStartup, 04-08 DeployDbStartup, 04-09 WorkspaceBackupService/RestoreStaging, 04-10 UniqueIndexGuard
provides:
  - Desktop startup through WorkspaceStartup.Open (quarantine notice, upgrade-failure page)
  - ADS_DATA_DIR data-root override
  - AdsHostFeatures.WorkspaceDataLabel / OpenWorkspaceData seam and the "Workspace data" menu item
  - WorkspaceDataDrawerTemplate (notice, outcomes, backup list, back up now, restore, reveal)
  - StartupFailurePage, FolderReveal, VerifyDesktopWorkspace.sh
  - Daily backup scheduler tied to process lifetime
affects: [04-12, 04-13]

plan_head_before: 18afc1633a7ed201a9b7a5663222b53344a18071
plan_head_after: fa4c337b48ce4c04aa2d42bac8a243976e3f783d

actuals:
  tokens: 7900   # chars/4 over git diff 18afc16..HEAD -- src (31,867 chars)
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "Code-only native failure page, reachable before any service exists (CreateWindow checks MauiProgram.StartupFailure first)"
    - "Host seam pair (label + opener) mirroring TenantScan; the browser host leaves it null so the menu item never appears"
    - "Test-only data-root override via an environment variable, defaults unchanged"

key-files:
  created:
    - src/AzureDesignStudio.Desktop/StartupFailurePage.cs
    - src/AzureDesignStudio.Desktop/FolderReveal.cs
    - src/AzureDesignStudio.Desktop/Components/WorkspaceDataDrawerTemplate.razor
    - src/VerifyDesktopWorkspace.sh
  modified:
    - src/AzureDesignStudio.Desktop/MauiProgram.cs
    - src/AzureDesignStudio.Desktop/App.xaml.cs
    - src/AzureDesignStudio.Desktop/MainPage.xaml.cs
    - src/AzureDesignStudio/Services/AdsHostFeatures.cs
    - src/AzureDesignStudio/Components/TopMenu.razor
    - src/AzureDesignStudio/Components/TopMenu.razor.cs

key-decisions:
  - "A WorkspaceUpgradeException (or a deploy.db InvalidOperationException) sets MauiProgram.StartupFailure and the host returns a bare built app; App.CreateWindow shows StartupFailurePage before touching any service"
  - "The backup service's error callback logs through the container's ILoggerFactory once Services exists, with the crash-log file as the fallback before that"
  - "The picker has no file-type filter; the restore verification rejects non-databases"
  - "The scheduler is skipped when a startup failure is set"

patterns-established:
  - "Reveal/open actions pass the path as one ProcessStartInfo.ArgumentList entry with UseShellExecute=false, and only for paths under MauiProgram.DataDirectory"

requirements-completed: []

duration: ~35min
completed: 2026-11-14
---

# Phase 4 Plan 11: Desktop workspace startup, data drawer and scheduler Summary

**The Desktop app now opens its workspace through WorkspaceStartup (corrupt file quarantined with a native notice, failed upgrade routed to a native failure page), exposes backup/restore in a "Workspace data" drawer, runs the daily backup loop, and can be exercised safely with ADS_DATA_DIR.**

## Performance

- **Tasks:** 3 (1 tracer, 2 auto), all committed individually
- **Files:** 4 created, 6 modified

## Accomplishments

- `MauiProgram` resolves the data root (`ADS_DATA_DIR` or the platform folder), calls `WorkspaceStartup.Open`, registers the report and `WorkspaceBackupService`, and starts `RunLoopAsync` on the thread pool with cancellation on `ProcessExit`.
- `StartupFailurePage`, `FolderReveal`, the `MainPage` recovery alert (`DisplayAlertAsync`, once per process) and the `App.CreateWindow` early return.
- Host seam + conditional "workspace" menu item; the drawer lists only workspace backups (the `WorkspaceBackupService.List()` stem filter), never the deploy.db ones.
- `src/VerifyDesktopWorkspace.sh <l2|l3|l4|corrupt>` launches the built Mac Catalyst app against a throwaway data directory.

## Task Commits

1. Task 1 (tracer): `b84cd70` - startup through WorkspaceStartup, failure page, ADS_DATA_DIR, verification script
2. Task 2: `a383106` - host seam, menu item and drawer
3. Task 3: `fa4c337` - scheduler start/stop and deploy-database failure routing

## Verification (evidence)

- `dotnet build src/AzureDesignStudio.Desktop -f net10.0-maccatalyst`: Build succeeded, 0 errors (the one warning, the missing `PublishFolderType` for `AzureDesignStudio.runtimeconfig.json`, is pre-existing SDK noise).
- `dotnet build src/AzureDesignStudio -c Release -p:RunAOTCompilation=false`: 0 warnings, 0 errors.
- Scripted launch of the built app in a GUI (Aqua) session, each against a fresh `mktemp -d` data directory via `ADS_DATA_DIR`; the app was killed and the directory deleted afterwards. The user's real data was not touched.
  - `corrupt`: `workspace.corrupt-<UTC>.db` created beside a new `workspace.db` with 1 history row. OUTCOME REACHED (re-run after Task 3: reached).
  - `l2`: premigration backup + manifest, history rows 1, Designs 3 / Scans 2 / ChangeFeed 3. OUTCOME REACHED (re-run after Task 3: reached).
  - `l3`: same as l2. OUTCOME REACHED.
  - `l4`: premigration backup, `upgrade-removed-rows-*.jsonl` report, Designs 2 after dedupe. OUTCOME REACHED.
- `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"`: 1772 passed, 0 failed.
- `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false`: 208 passed, 8 skipped (SQL Server only), 0 failed.
- Acceptance greps: no `async void` in the drawer, one `FilePicker.Default.PickAsync`, no CommunityToolkit, no `backups/deploy` in the drawer, `git ls-files -s` shows 100755 for the script, no `packages.lock.json` modified.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Verification script polled with `sqlite3 -readonly`**
- **Found during:** Task 1 (l2/l3/l4 runs reported "history rows: 0" although the app had upgraded)
- **Issue:** A read-only open of a WAL-mode database that has no `-shm` file fails with "unable to open database file", so the poll never saw the migration history.
- **Fix:** Poll and report with a normal connection and a 2 s busy timeout.
- **Files modified:** src/VerifyDesktopWorkspace.sh
- **Commit:** b84cd70

**2. [Rule 1 - Bug] Script hardcoded the executable name**
- **Found during:** Task 1
- **Issue:** The bundle executable is `AzureDesignStudio.Desktop`, not `Azure Design Studio`.
- **Fix:** Read `CFBundleExecutable` from `Info.plist` with PlistBuddy.
- **Commit:** b84cd70

### Plan adjustments (not bugs)

- `WorkspaceDb` is not injected into the drawer (nothing in it needs the database; the service and report cover every section). The plan listed it among the injects.
- The `MainPage.Loaded` handler is `async void`: a MAUI lifecycle `EventHandler` cannot return a Task. The AGENTS.md anti-pattern is about Blazor handlers; the body is wrapped in try/catch and logs instead of throwing. No `async void` exists in the Blazor drawer.
- A `Restore` outcome that was staged but not applied (`Applied: false` with a `Problem`) also gets an alert and a drawer banner.

**Total deviations:** 2 auto-fixed bugs (both in the new script), 3 small adjustments. No architectural changes.

## Issues Encountered / Not verified here

- The Windows MAUI target cannot be compiled on this Mac; the Windows branch of `FolderReveal` (`explorer.exe`, `/select,<path>`) and the `FileSystem.Current.AppDataDirectory` path are unbuilt and untested.
- The visual parts were not exercised by a human: the native "Workspace recovered" alert on first window load, the failure page, the "Workspace data" menu item and drawer, the file picker, Reveal, and the pending-restore banner with Quit. The scripted launch proves the data outcomes only. These belong to the human-verification plan (04-13).
- The failure page path (a `WorkspaceUpgradeException` at launch) is covered by compile and by the unit tests of 04-07/04-10, not by a scripted launch.
- Killing the app with SIGTERM while the first scheduled backup was being written left a `workspace-*-scheduled.db.tmp` in the scratch directory once (a normal artifact of a kill mid-backup; the scratch directory was deleted).

## Known Stubs

None.

## Threat Flags

None beyond the plan's register: the reveal action is the only new process start (single `ArgumentList` argument, `UseShellExecute = false`, path must exist under the data directory), and `ADS_DATA_DIR` is accepted as per T-04-43.

## Next Phase Readiness

04-12 (documentation/inventory) and 04-13 (human verification) can proceed; 04-13 should cover the visual items listed above.

## Self-Check: PASSED

- Created files exist: StartupFailurePage.cs, FolderReveal.cs, WorkspaceDataDrawerTemplate.razor, VerifyDesktopWorkspace.sh
- Commits b84cd70, a383106, fa4c337 are ancestors of HEAD

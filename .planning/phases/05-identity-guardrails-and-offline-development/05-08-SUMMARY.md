---
phase: 05-identity-guardrails-and-offline-development
plan: 08
subsystem: database
tags: [approvals, tenant-isolation, ef-migrations, sql-server, sqlite, grpc, xunit]

requires:
  - phase: 05-identity-guardrails-and-offline-development
    provides: "AdsCaller.ScopeId and the scope-aware WhatIfTokenGate.TryPeekPlan that yields the preview's requester (plans 05-01, 05-06)"
  - phase: 05-identity-guardrails-and-offline-development
    provides: "TenantGuardrails migration pattern, deploy.db Baseline-era test shape, SQL Server container proof (05-04)"
provides:
  - "PlanApprovalModel keyed by (TenantId, PlanHash) with RequestedByUserId; DecomApprovalModel keyed by (TenantId, ExecutionHash)"
  - "TenantApprovals migration on SQL Server (hand-guarded add, backfill, not-null, key rebuild) and SQLite (as generated)"
  - "ApprovePlan, CreateDeployment, CreateStack, ApproveDecomWindow and ExecuteDecomWindow store and find approvals in the caller's scope only"
  - "Pending legacy approvals backfilled with the empty-GUID scope: they fail closed and must be re-approved"
  - "Approval and decommission isolation facts in TenantIsolationTests; SQL Server container proof of the key rebuild"
affects: [05-09, 05-10, 05-11]

actuals:
  tokens: 21000
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "Composite primary key declared in OnModelCreating (key order drives FindAsync([scopeId, hash]))"
    - "SQL Server key rebuild as one guarded migrationBuilder.Sql per step: add nullable, UPDATE backfill, ALTER NOT NULL, drop the old PK by name via sys.key_constraints + QUOTENAME + EXEC, add the named composite PK"
    - "Migration statements exposed as internal const strings so a container test can replay them on an upgraded database"

key-files:
  created:
    - src/AzureDesignStudio.Server/Persistence/Migrations/20261114160048_TenantApprovals.cs
    - src/AzureDesignStudio.Server/Persistence/Migrations/20261114160048_TenantApprovals.Designer.cs
    - src/AzureDesignStudio.Services/Migrations/Sqlite/20261114160052_TenantApprovals.cs
    - src/AzureDesignStudio.Services/Migrations/Sqlite/20261114160052_TenantApprovals.Designer.cs
  modified:
    - src/AzureDesignStudio.Services/Models/DesignDbContext.cs
    - src/AzureDesignStudio.Services/DeployService.cs
    - src/AzureDesignStudio.Services/Migrations/Sqlite/SqliteDesignDbContextModelSnapshot.cs
    - src/AzureDesignStudio.Server/Persistence/Migrations/SqlServerDesignDbContextModelSnapshot.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs
    - src/AzureDesignStudio.Server.Tests/Persistence/DeployDbMigrationTests.cs
    - src/AzureDesignStudio.Server.Tests/Persistence/SqlServerMigrationModelTests.cs
    - src/AzureDesignStudio.Server.Tests/Persistence/SqlServerMigrationTests.cs

key-decisions:
  - "The empty-GUID backfill is an UPDATE ... WHERE TenantId IS NULL, never a column default, so no model-unknown default constraint exists on SQL Server; it also makes a second run harmless (a real scope's row is never dragged into the empty one)"
  - "The SQLite migration stays exactly as EF generated it: its AddColumn defaultValue is applied only while copying existing rows during the table rebuild and the rebuilt table carries no default, so the fresh-vs-model parity fact passes unchanged"
  - "Isolation tests drive CreateStack as well as CreateDeployment: the stack path shares the same scope-keyed FindAsync and runs on the existing ScriptedArm without a streaming writer"

patterns-established:
  - "Legacy approvals are represented in tests by a row with TenantId = Guid.Empty; no real caller (organization, personal account or another tenant) finds it"

requirements-completed: []

coverage:
  - id: D1
    description: "Two scopes approve the same plan hash and the same decommission execution hash without reading or overwriting each other; a second approval replaces only the approving scope's row"
    requirement: "IDA-05"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#TwoScopesApproveTheSamePlanHashAndNeitherOverwritesTheOther"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#TwoScopesApproveTheSameDecommissionExecutionAndEachOnlyExecutesItsOwn"
        status: pass
    human_judgment: false
  - id: D2
    description: "Deploying, stacking and executing a decommission window find only the caller's scope approval (PLAN_NOT_APPROVED / 412 DECOM_APPROVAL_MISSING for another scope); a token issued in one scope is stale for another and still works for its owner"
    requirement: "IDA-05"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#ADeploymentFindsOnlyTheCallersScopeApprovalAndALegacyRowFailsClosed"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#AnotherScopesApprovalIsNotFoundAndTheOwnersTokenStillWorksForTheOwner"
        status: pass
    human_judgment: false
  - id: D3
    description: "A legacy approval in the empty-GUID scope is found by no real caller (organization, personal account, other tenant) and is neither consumed nor adopted; re-approving creates the caller's own row"
    requirement: "IDA-05"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#ALegacyApprovalInTheEmptyScopeIsFoundByNoRealCaller"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#ALegacyDecommissionApprovalInTheEmptyScopeIsFoundByNoRealCaller"
        status: pass
    human_judgment: false
  - id: D4
    description: "The approval records the preview's requester (not the approver) and the caller's scope"
    requirement: "IDA-05"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#TheApprovalRecordsTheRequesterOfThePreviewNotTheApprover"
        status: pass
    human_judgment: false
  - id: D5
    description: "TenantApprovals upgrades a pre-tenant deploy.db without losing a row (empty-GUID scope, composite keys, parity with a model-created file)"
    requirement: "IDA-05"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Persistence/DeployDbMigrationTests.cs#PreTenantFile_KeepsItsApprovals_InTheEmptyScope_AndGainsCompositeKeys"
        status: pass
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Persistence/DeployDbMigrationTests.cs#FreshFile_MigratesAndMatchesAModelCreatedFile"
        status: pass
    human_judgment: false
  - id: D6
    description: "TenantApprovals upgrades a legacy SQL Server database without losing a row: composite named keys, empty-GUID scope, TenantId NOT NULL with no default, schema equal to a fresh database and to the model, statements safe to run again"
    requirement: "IDA-05"
    verification:
      - kind: integration
        ref: "bash src/RunSqlServerTests.sh (mcr.microsoft.com/mssql/server:2022-latest, amd64 emulation): 11 passed, 0 skipped"
        status: pass
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Persistence/SqlServerMigrationModelTests.cs#TenantApprovalsScriptAddsBackfillsRequiresAndRekeysEveryStepGuarded"
        status: pass
    human_judgment: false

duration: 40min
completed: 2026-11-14
status: complete
plan_head_before: 9ae014c8d6e962b01a55e060815bfbda238741db
plan_head_after: 5dab6b231b54ea4aac03389f19d0ee82a6ff698d
commits: 3
---

# Phase 5 Plan 08: Tenant-Scoped Plan and Decommission Approvals Summary

**Plan and decommission approvals are keyed by (caller scope, hash) through a TenantApprovals migration on SQL Server and SQLite, so one scope can neither find nor overwrite another's approval; approvals pending at upgrade are backfilled with the empty-GUID scope and fail closed.**

## Performance

- **Duration:** about 40 min (the SQL Server container start and run under amd64 emulation dominated Task 3)
- **Tasks:** 3 (1 tracer, 1 TDD, 1 container proof)
- **Files:** 12 changed (4 created, 8 modified), of which 4 are EF-generated Designer/snapshot files

## Accomplishments

- Task 1 (tracer): `PlanApprovalModel` and `DecomApprovalModel` lost their `[Key]` attributes and gained `TenantId` (first half of the composite key, declared in `OnModelCreating`); plan approvals gained `RequestedByUserId` (max 64). `ApprovePlan` takes the requester from the scope-aware peek of plan 05-06 and replaces only the caller's earlier row for the same hash. `CreateDeployment`, `CreateStack`, `ApproveDecomWindow` and `ExecuteDecomWindow` use `FindAsync([caller.ScopeId, hash])`; the approver comparisons and their codes are untouched. The SQLite `TenantApprovals` migration is as generated. The SQL Server `Up` is eleven guarded `migrationBuilder.Sql` calls (six on `AdsPlanApprovals`, five on `AdsDecomApprovals`): `COL_LENGTH`-guarded nullable add, `UPDATE ... WHERE TenantId IS NULL` backfill to the empty GUID, `ALTER COLUMN ... NOT NULL` behind a `COLUMNPROPERTY` check, drop of the old primary key found by name in `sys.key_constraints` (skipped when it already includes `TenantId`; `QUOTENAME` then `EXEC`), and a named `PK_<table>` composite key when no primary key exists. No default constraint, empty `Down`, Baseline/NormaliseLegacyDefaults/TenantGuardrails untouched.
- Task 2 (TDD): eight facts added to `TenantIsolationTests`, covering plan and decommission isolation, requester recording, legacy empty-scope rows (plan and decom, found by an organization member, another tenant and a personal account), the deployment path (`CreateDeployment` over scripted ARM with a collecting stream writer), a stack token stale for another scope but good for its owner, and replacement of only the approving scope's row. Callers carry Guardrail.Admin and Deploy.Approver and the requester differs from the approver, so the facts stay true when plan 05-09 lands. A mutation check (reading decom approvals by hash only) made exactly the two decommission facts fail.
- Task 3: `SqlServerMigrationTests` asserts, on the legacy fixture, that every seeded approval row is kept in the empty-GUID scope, `TenantId` is NOT NULL with no default constraint, `RequestedByUserId` is nullable nvarchar(64), and the keys are the named composite `PK_AdsPlanApprovals (TenantId, PlanHash)` and `PK_AdsDecomApprovals (TenantId, ExecutionHash)`. Two new facts: a fresh database admits the same hash in two scopes and refuses it twice in one; replaying every migration statement on an upgraded database changes no row, key or scope assignment. The existing legacy-vs-fresh and fresh-vs-model parity facts and the backup/restore rehearsal pass unchanged.

## Task Commits

1. **Task 1: scope-keyed approvals and the TenantApprovals migration** - `0bf4b55` (feat)
2. **Task 2: approval isolation and fail-closed legacy rows** - `08e86cc` (test)
3. **Task 3: SQL Server proof of the TenantApprovals key rebuild** - `5dab6b2` (test)

The tracer gate (Task 1's offline filter, 89 then 90 passed) was re-run end to end before expanding. Task 2's facts passed on the first run because Task 1 already contained the behavior; the mutation check above confirmed they bite. The plan prescribes one commit per task, so there is no separate RED commit.

## Decisions Made

See `key-decisions` above. Operator consequence for plan 05-10's docs: after the upgrade every approval that was pending is in the empty-GUID scope and must be approved again; nothing is lost, and nothing matches.

## Deviations from Plan

### Auto-fixed / additive

**1. [Rule 2 - Missing critical] Offline script fact for the SQL Server migration**
- **Found during:** Task 1
- **Issue:** the plan's tests exercised the SQLite upgrade offline but nothing offline guarded the hand-written T-SQL (key_constraints lookup, backfill, no DEFAULT) the way `TenantGuardrailsScriptGuardsTheColumnTheIndexAndTheTables` does for the previous migration.
- **Fix:** `TenantApprovalsScriptAddsBackfillsRequiresAndRekeysEveryStepGuarded` in `SqlServerMigrationModelTests.cs` (not named in the plan's `files_modified`).
- **Commit:** 0bf4b55

**2. [Scope note] Two extra container facts**
- The plan asked for assertions on the legacy upgrade; two further facts (fresh database admits one hash in two scopes; replaying the statements is a no-op) were added to `SqlServerMigrationTests.cs`, which the plan already names. The container count went from 9 to 11 passed.

**Total deviations:** 1 additive test file touch, 1 scope note. **Impact:** none on behavior.

## Issues Encountered

- `dotnet ef migrations add` needs the MSBuild property after `--` (`-- -p:RunAOTCompilation=false`), as noted in 05-04.
- SDK 10.0.403 vs pinned 10.0.401: no `packages.lock.json` drift appeared in any run (`git status --porcelain -- '*packages.lock.json'` is empty).
- C# interpolated raw strings reject `{{"...` JSON as content; the SQLite fixture uses plain placeholder text for `PlanJson` instead.

## Known Stubs

None.

## Threat Flags

None. The only new surface is a column and a changed key on tables the threat model already covers (T-05-22, T-05-23, T-05-24, T-05-26 mitigated as planned).

## Verification

- Task 1 filter (DeployDbMigrationTests, SqlServerMigrationModelTests, TenantIsolationTests, StackRpcTests, DeployServiceTests, DatabaseBootstrapperTests): 90 passed, 0 failed. Task 2 filter (TenantIsolationTests, StackRpcTests): 53 passed.
- `bash src/RunSqlServerTests.sh` ran for real against `mcr.microsoft.com/mssql/server:2022-latest` (Docker 29.8.2, amd64 emulation): 11 passed, 0 skipped. This is the first execution of the primary-key rebuild T-SQL; it succeeded on the legacy fixture (unnamed system-generated keys) and on a fresh database. The container was removed afterwards (no `ads-sqltest-*` left; unrelated containers untouched).
- `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false`: 441 passed, 0 failed, 11 skipped (the Category=SqlServer facts, skipped by design without `ADS_TEST_SQLSERVER`; they ran in the container run above).
- `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"`: 1820 passed, 0 failed.
- Acceptance greps: one `*_TenantApprovals.cs` per provider; `key_constraints` in the SQL Server migration = 4; Baseline and NormaliseLegacyDefaults unchanged; no `packages.lock.json` changes.
- The Windows MAUI Desktop target cannot be compiled on this Mac and was not built. Desktop code is untouched; it reaches the changed behavior through `DeployService` and `DeployDbStartup`, which the Server tests exercise (including the premigration backup of a pre-tenant `deploy.db` and the new pre-tenant approvals upgrade).

## Self-Check: PASSED

- FOUND: src/AzureDesignStudio.Server/Persistence/Migrations/20261114160048_TenantApprovals.cs
- FOUND: src/AzureDesignStudio.Services/Migrations/Sqlite/20261114160052_TenantApprovals.cs
- FOUND: src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs
- FOUND commits: 0bf4b55, 08e86cc, 5dab6b2

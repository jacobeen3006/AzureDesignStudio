---
phase: 05-identity-guardrails-and-offline-development
plan: 04
subsystem: database
tags: [guardrails, tenant-isolation, ef-migrations, sql-server, sqlite, grpc, xunit]

requires:
  - phase: 05-identity-guardrails-and-offline-development
    provides: "AdsCaller.ScopeId (tid, or oid for the personal-account tenant) and the typed caller on every Design and Deploy RPC (plans 05-01, 05-02)"
  - phase: 04-database-durability
    provides: "Migration baselines, guarded T-SQL conventions, deploy.db migration set, SQL Server container proof (04-03, 04-06, 04-08)"
provides:
  - "TenantGuardrails migration on SQL Server (hand-guarded Up) and SQLite (as generated)"
  - "AdsGuardrailPolicies.TenantId (nullable, unique; filtered to non-NULL on SQL Server); Id=1 NULL-tenant row retained as the inherited default"
  - "AdsTenantSettings and AdsSecurityEvents tables plus TenantSettingsModel, SecurityEventModel, GuardrailPolicyModes, SecurityEventTypes (written by plans 05-06 and 05-07)"
  - "Scope-keyed GetGuardrailPolicy/SetGuardrailPolicy and DeployService.LoadGuardrailPolicy(scopeId, ct) used by WhatIf, CreateDeployment, the stack preparation and ExecuteDecomWindow"
  - "TenantIsolationTests (guardrail half) and a SQL Server container proof of the upgrade"
affects: [05-05, 05-06, 05-07, 05-08, 05-10, 05-11]

actuals:
  tokens: 14000
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "Guarded T-SQL Up as internal const strings, one migrationBuilder.Sql per statement so an index can name a column an earlier call added"
    - "deploy.db migration tests start from a Baseline-era file (migrate to the Baseline only, optionally drop the history) instead of a model-created file; history rows compared with the migration count, never a literal"
    - "Policy resolution = scope row, else NULL-tenant default row, else empty; unreadable body fails closed for the resolving scope only"

key-files:
  created:
    - src/AzureDesignStudio.Services/Models/TenantModels.cs
    - src/AzureDesignStudio.Services/Migrations/Sqlite/20261114153430_TenantGuardrails.cs
    - src/AzureDesignStudio.Services/Migrations/Sqlite/20261114153430_TenantGuardrails.Designer.cs
    - src/AzureDesignStudio.Server/Persistence/Migrations/20261114153438_TenantGuardrails.cs
    - src/AzureDesignStudio.Server/Persistence/Migrations/20261114153438_TenantGuardrails.Designer.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs
  modified:
    - src/AzureDesignStudio.Services/Models/DesignDbContext.cs
    - src/AzureDesignStudio.Services/DeployService.cs
    - src/AzureDesignStudio.Services/Migrations/Sqlite/SqliteDesignDbContextModelSnapshot.cs
    - src/AzureDesignStudio.Server/Persistence/Migrations/SqlServerDesignDbContextModelSnapshot.cs
    - src/AzureDesignStudio.Server.Tests/Persistence/DeployDbMigrationTests.cs
    - src/AzureDesignStudio.Server.Tests/Persistence/SqlServerMigrationModelTests.cs
    - src/AzureDesignStudio.Server.Tests/Persistence/SqlServerMigrationTests.cs
    - src/AzureDesignStudio.Server.Tests/Persistence/SqlServerSchemaDescriber.cs

key-decisions:
  - "A scope's own row is created with Id = max(existing Id, 1) + 1 (the table keeps explicit keys); a concurrent first write that loses on the unique index or the key clears the tracker and upserts once more"
  - "The inherited default is recognised as TenantId IS NULL AND Id = 1, so a stray NULL-tenant row with another id never governs anyone"
  - "An empty stored body for a scope's own row means the empty policy (as today) and does not fall through to the default; only a missing row inherits"

patterns-established:
  - "LoadGuardrailPolicy is internal and takes the scope id, so tests call it for the scope each caller passes and plan 05-07 layers the merge mode on this one method"
  - "SQL Server schema describer reports index filter definitions, so parity checks cover the filtered unique index text"

requirements-completed: []

coverage:
  - id: D1
    description: "Two organizational tenants and two personal accounts each write and read their own guardrail policy; none reads or overwrites another's; two members of one organization share one policy; an organization and a personal account are separate scopes"
    requirement: "IDA-05"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#TwoOrganizationsKeepTheirOwnPolicies"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#TwoPersonalAccountsAreTwoScopesEvenThoughTheyShareATenant"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#AnotherTenantsWriteNeverAltersARow"
        status: pass
    human_judgment: false
  - id: D2
    description: "The legacy Id=1 NULL-tenant row stays the inherited default: a scope with no row reads it, its own write replaces it, and SetGuardrailPolicy never writes it"
    requirement: "IDA-05"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#AScopeWithNoRowOfItsOwnReadsTheDefaultAndItsOwnWriteReplacesIt"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#SetNeverWritesTheNullTenantDefaultRow"
        status: pass
    human_judgment: false
  - id: D3
    description: "Every enforcement path loads the caller's scope policy: a stack what-if for one scope is blocked by that scope's policy and not by another's; an unreadable body fails closed for the affected scope only"
    requirement: "IDA-05"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#AStackWhatIfIsBlockedByTheCallersPolicyAndNotByAnotherScopes"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs#AnUnreadableBodyFailsClosedForTheAffectedScopeOnly"
        status: pass
    human_judgment: false
  - id: D4
    description: "TenantGuardrails upgrades a legacy SQL Server database and a pre-tenant deploy.db without losing a row; the upgraded schema equals a fresh database and the model"
    requirement: "IDA-05"
    verification:
      - kind: integration
        ref: "bash src/RunSqlServerTests.sh (mcr.microsoft.com/mssql/server:2022-latest, amd64 emulation): 9 passed, 0 skipped"
        status: pass
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Persistence/DeployDbMigrationTests.cs#PreTenantFile_KeepsItsGuardrailRow_AndGainsTheTenantTables"
        status: pass
    human_judgment: false

duration: 45min
completed: 2026-11-14
status: complete
plan_head_before: 7449e2752cbaca5b764373e92492d73916a117bc
plan_head_after: 293d9df03695f3b77410d9e3c8240b6f43c40c09
commits: 3
---

# Phase 5 Plan 04: Per-Tenant Guardrail Policy Storage and Enforcement Summary

**Guardrail policies are now stored per scope (tenant id, or the user's object id for a personal account) through a legacy-safe TenantGuardrails migration on SQL Server and SQLite, and WhatIf, CreateDeployment, the stack what-if/create and ExecuteDecomWindow judge a plan against the caller's own scope, falling back to the retained NULL-tenant Id=1 row.**

## Performance

- **Duration:** about 45 min (the SQL Server container start under amd64 emulation dominated Task 3)
- **Tasks:** 3 (1 tracer, 1 TDD, 1 container proof)
- **Files:** 14 changed (6 created, 8 modified), of which 4 are EF-generated Designer/snapshot files

## Accomplishments

- Task 1 (tracer): `GuardrailPolicyModel.TenantId` (`Guid?`, unique index), `AdsTenantSettings` and `AdsSecurityEvents` with `TenantModels.cs`. SQLite migration kept as generated. The SQL Server `Up` is five guarded commands (`COL_LENGTH` column add, `INDEXPROPERTY` filtered unique index, two `OBJECT_ID`-guarded `CREATE TABLE`, the events index) with no default constraint and an empty `Down`; Designer and snapshot are the generated files, earlier migrations are untouched.
- `DeployDbMigrationTests` now starts old-file emulations from the Baseline-era shape (migrate to the Baseline through `IMigrator`, optionally drop the history table); a new fact proves a pre-tenant `deploy.db` upgrades with its guardrail row intact and `TenantId` NULL and gains two empty tables; another proves the unique index admits many NULL rows and refuses a tenant twice. Table counts 7 to 9, history rows compared with the migration count.
- Task 2 (TDD): Get resolves own row, else default; Set upserts the caller's row only (never the default), with one retry after a lost unique-index race; `LoadGuardrailPolicy(scopeId, ct)` is internal and used by the four enforcement paths. `TenantIsolationTests` has 17 facts, including the stack what-if run twice against the scripted ARM with two scopes.
- Task 3: `SqlServerMigrationTests` asserts the two new tables exist and are empty, `TenantId` is nullable, the seeded row keeps a NULL tenant, the filtered unique index exists, and no default constraint is left behind. `bash src/RunSqlServerTests.sh` ran for real against `mcr.microsoft.com/mssql/server:2022-latest`: 9 passed, 0 skipped (8 at HEAD plus the new filtered-index fact); legacy-vs-fresh and fresh-vs-model parity pass unchanged, so the hand-written guarded SQL equals the model.

## Task Commits

1. **Task 1: TenantGuardrails schema on both providers** - `589ce90` (feat)
2. **Task 2: scope-keyed guardrail policy storage and enforcement** - `f6f03c1` (feat)
3. **Task 3: SQL Server container proof** - `293d9df` (test)

The tracer gate was re-run end to end before expanding (54 passed). Task 2's tests were written first and failed to compile against the private loader (RED), then passed after the change (GREEN); the plan prescribes one commit per task, so there is no separate RED commit.

## Decisions Made

See `key-decisions` above. The default row is recognised as `TenantId IS NULL AND Id = 1`; an own row with an empty body is the empty policy and does not inherit.

## Deviations from Plan

### Auto-fixed / additive

**1. [Rule 2 - Missing critical] Schema describer did not report index filters**
- **Found during:** Task 3
- **Issue:** `SqlServerSchemaDescriber.Indexes` ignored `filter_definition`, so the legacy-vs-fresh and fresh-vs-model parity checks could not detect a wrong filtered-index text.
- **Fix:** include `ISNULL(i.filter_definition, '')` in the index line (and group by it).
- **Files modified:** src/AzureDesignStudio.Server.Tests/Persistence/SqlServerSchemaDescriber.cs (not named in the plan's `files_modified`)
- **Verification:** container run, parity facts pass with the filter compared.
- **Commit:** 293d9df

**2. [Scope note] Offline guard against defaults reads the migration's own texts**
- The plan asks that no model-unknown default be left behind. `GenerateScript()` as a whole contains the word `default_constraints` from `NormaliseLegacyDefaults`, so the offline fact checks the five `TenantGuardrails` SQL constants for `DEFAULT`, and the container test checks `sys.default_constraints` on the three tables.

**Total deviations:** 1 additive test-helper change, 1 clarification. **Impact:** none on behavior.

## Issues Encountered

- `dotnet ef` needs the MSBuild property after `--` (`-- -p:RunAOTCompilation=false`); passing it before is rejected by the tool.
- SDK 10.0.403 vs pinned 10.0.401: no `packages.lock.json` drift appeared in any of the runs (`git status --porcelain -- '*packages.lock.json'` is empty).

## Known Stubs

None. `AdsTenantSettings` and `AdsSecurityEvents` are deliberately empty until plans 05-06 and 05-07 write them (documented in the model comments); nothing renders them.

## Threat Flags

None. The only new surface is two tables and a column the plan's threat model already covers (T-05-10, T-05-11, T-05-12 mitigated; T-05-14 accepted as planned).

## Verification

- Plan filter for Task 1: 54 passed. Plan filter for Task 2: 72 passed, 0 failed.
- `bash src/RunSqlServerTests.sh`: 9 passed, 0 skipped; container removed afterwards (no `ads-sqltest-*` left, unrelated containers untouched).
- `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false`: 342 passed, 0 failed, 9 skipped (the Category=SqlServer facts, skipped by design without `ADS_TEST_SQLSERVER`; they ran in the container run above).
- Acceptance greps: `Guid? TenantId` in DesignDbContext = 1; one `*_TenantGuardrails.cs` per provider; `COL_LENGTH` in the SQL Server migration = 2; Baseline and NormaliseLegacyDefaults unchanged; `ScopeId` in DeployService = 7; no `packages.lock.json` changes.
- The Windows MAUI Desktop target cannot be compiled on this Mac and was not built. Desktop code is untouched; it reaches the changed behavior through `DeployService` and `DeployDbStartup`, which the Server tests exercise (including a premigration backup of a pre-tenant `deploy.db`).
- Behavior note for Desktop users: a single-user host's first `SetGuardrailPolicy` creates its own scoped row; the old Id=1 row stays and keeps governing any scope without a row (D-09).

## Self-Check: PASSED

- FOUND: src/AzureDesignStudio.Services/Models/TenantModels.cs
- FOUND: src/AzureDesignStudio.Server.Tests/Hosting/TenantIsolationTests.cs
- FOUND: src/AzureDesignStudio.Server/Persistence/Migrations/20261114153438_TenantGuardrails.cs
- FOUND: src/AzureDesignStudio.Services/Migrations/Sqlite/20261114153430_TenantGuardrails.cs
- FOUND commits: 589ce90, f6f03c1, 293d9df

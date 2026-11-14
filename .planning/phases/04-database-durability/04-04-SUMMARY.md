---
phase: 04-database-durability
plan: 04
subsystem: database
tags: [sqlite, wal, efcore, mcp, concurrency, connection-interceptor]
status: complete

requires:
  - phase: 04-database-durability
    provides: 04-02 WorkspaceSchema.UpgradeAsync (baseline migration, WAL assertion)
provides:
  - WorkspaceConnection (writer/reader factory, no shared cache), PragmaInterceptor, PragmaProfile, WorkspaceOpenMode, WorkspaceReaderOpen
  - AdsWorkspace opening through the reader ladder, exposing OpenMode and OpenDetail
  - "ads-mcp --db <path> --self-check <seconds>" (exit 0 clean, 3 on read errors, 2 on open failure) and stderr report of the open mode
  - WorkspaceConnectionTests, AdsWorkspaceOpenTests, WorkspaceConcurrencyTests, UnixNonRootFactAttribute
affects: [04-06, 04-08, 04-12]

plan_head_before: c9df4c2974d7d01f4148e5e80f2c29a78cbf7308
plan_head_after: 4e525dcaa44a5efb0c660c50618cb29f5d01f653

actuals:
  tokens: 11500   # chars/4 over the 13-file diff (lock file excluded)
  tasks: 3
  commits: 3

tech-stack:
  added: []
  patterns:
    - "One static stateless PragmaInterceptor per profile (a new instance per context would grow EF's internal service-provider cache)"
    - "Reader ladder with an injectable probe delegate so each rung and refusal tests without a real permission failure"
    - "Only SQLite codes 8 (readonly) and 14 (cantopen) descend the ladder; anything else throws with the code"

key-files:
  created:
    - src/AzureDesignStudio.Workspace/WorkspaceConnection.cs
    - src/AzureDesignStudio.Mcp/AdsSelfCheck.cs
    - src/AzureDesignStudio.Core.Tests/WorkspaceConnectionTests.cs
    - src/AzureDesignStudio.Core.Tests/AdsWorkspaceOpenTests.cs
    - src/AzureDesignStudio.Core.Tests/WorkspaceConcurrencyTests.cs
    - src/AzureDesignStudio.Core.Tests/Support/UnixNonRootFactAttribute.cs
  modified:
    - src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs
    - src/AzureDesignStudio.Workspace/WorkspaceSchema.cs
    - src/AzureDesignStudio.Mcp/AdsWorkspace.cs
    - src/AzureDesignStudio.Mcp/Program.cs
    - src/AzureDesignStudio.Core.Tests/AzureDesignStudio.Core.Tests.csproj
    - src/AzureDesignStudio.Core.Tests/packages.lock.json

key-decisions:
  - "synchronous stays at the durable default (FULL, asserted by a test); journal_mode is never set per open"
  - "The reader never creates the file: OpenReader refuses a missing path before any rung, and rung 2 uses Mode=ReadWrite (not ReadWriteCreate)"
  - "The immutable rung is refused when a non-empty -wal exists, because it would read a stale picture of a live database"
  - "PragmaInterceptor takes a PragmaProfile (Writer, Reader, QueryOnlyReader) instead of a bool so the three pragma sets are explicit"

patterns-established:
  - "Connections come from WorkspaceConnection only; the shared-cache keyword appears nowhere in src"

requirements-completed: []  # DATA-03 and DATA-06 are closed by the orchestrator at phase completion

duration: 25min
completed: 2026-11-14
---

# Phase 04 Plan 04: One connection discipline for the workspace writer and the MCP reader Summary

**The workspace writer and the MCP reader are built by one factory without shared cache, the per-open pragmas ride a connection interceptor, the reader opens read-only with a reported fallback ladder, and a 15 s stress with a real second process produced zero BUSY/LOCKED errors.**

## Accomplishments

- `WorkspaceConnection`: `WriterConnectionString`/`WriterOptions` (Pooling, ForeignKeys, no cache keyword), `PragmaInterceptor` (busy_timeout 5000, foreign_keys, journal_size_limit 64 MiB for the writer; busy_timeout plus query_only for the fallback reader), and `OpenReader`/`ReaderOptions`.
- Reader ladder: `Mode=ReadOnly`, then `Mode=ReadWrite` plus `query_only`, then `immutable=1` (percent-encoded file URI, only without a live WAL). Corrupt (11), not-a-database (26) and busy (5/6) throw at once naming the code.
- `WorkspaceDb.CreateContext` and the `WorkspaceSchema` migrating context use `WriterOptions`; `AdsWorkspace` is a thin consumer exposing `OpenMode`/`OpenDetail`, and `HasTableAsync` reads through a context so the interceptor runs.
- `ads-mcp` writes `workspace opened <mode>: <detail>` to stderr, exits 2 with `cannot open workspace` on a failed open, and `--self-check <seconds>` runs the bounded read loop instead of the MCP host. The 15-tool surface is untouched and nothing new goes to stdout.
- Core.Tests references the Mcp project (regenerated Core.Tests lock only).

## Task Commits

1. **Task 1 (tracer): one connection factory, cold workspace opens read-only, pooled pragmas in force** - `ba24e51` (feat)
2. **Task 2: ladder behavior, ads-mcp open-mode report and self-check** - `74f8fd6` (feat)
3. **Task 3: 15 s writers-and-readers stress with a real second process** - `4e525dc` (test)

## Verification

- Tracer gate (auto, not blocking-human): `WorkspaceConnectionTests` 1 passed (journal_mode wal, busy_timeout 5000, foreign_keys 1, synchronous 2 on a pooled second connection; cold workspace reports `ReadOnly`); `WorkspaceTests|WorkspaceUpgradeTests|AdsToolEngineTests` 66 passed before expanding.
- `AdsWorkspaceOpenTests` 13 passed, 0 skipped (the read-only-folder test ran on this macOS machine and reported `Immutable`); `dotnet build src/AzureDesignStudio.Mcp -c Release` 0 warnings, 0 errors.
- `WorkspaceConcurrencyTests` 1 passed in 15 s: two page writers, two governance writers, two in-process readers and a real `dotnet exec` child (`--self-check 12`) with zero exceptions and `errors=0` from the child.
- Full offline suite `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"`: 1662 passed, 0 failed. `Server.Tests` `SourceGuardTests`: 5 passed.
- Acceptance greps: shared-cache keyword in Workspace/Mcp (and all of `src`) 0; `class PragmaInterceptor` 1; Mcp project reference 1; stdout writes in Program.cs/AdsSelfCheck.cs 0; `self-check` in Program.cs 3; `WithTools<AdsMcpTools>` 1; `ADS_STRESS_SECONDS` and `self-check` present in the stress test.
- Lock files: `dotnet restore` rewrote only `src/AzureDesignStudio.Core.Tests/packages.lock.json` (the Mcp graph's transitive packages); it is committed and no other lock changed.

## Deviations from Plan

None - the plan was executed as written. Two small shape choices inside the plan's freedom: `PragmaInterceptor` takes a `PragmaProfile` enum rather than a bool, and the interceptors are shared static instances.

## Not verified here

- The guard mutations named in the acceptance criteria (reintroducing the shared-cache keyword, printing the open mode with `Console.WriteLine`, removing `WithTools`) were not run as mutations; the counts were checked on the final code only.
- The read-only-directory test and the ladder were exercised on macOS only; Windows skips the directory test by design (`UnixNonRootFact`).
- The stress run does not by itself prove `busy_timeout` is what prevents BUSY: Microsoft.Data.Sqlite also applies its own 30 s default timeout. It proves the combined discipline (WAL, no shared cache, pragmas, provider defaults) stays error-free under this load.

## Known Stubs

None.

## Threat Flags

None. T-04-10 (stress plus second process), T-04-11 (read-only rung, `query_only` rung proven unable to INSERT, no Migrate in Mcp by the 04-03 guard) and T-04-13 (only codes 8/14 descend, 11/26 tested to throw at once) are implemented; T-04-12 stays accepted and is surfaced in `OpenDetail` and on stderr.

## Self-Check: PASSED

- FOUND: WorkspaceConnection.cs, AdsSelfCheck.cs, WorkspaceConnectionTests.cs, AdsWorkspaceOpenTests.cs, WorkspaceConcurrencyTests.cs, UnixNonRootFactAttribute.cs
- FOUND commits: ba24e51, 74f8fd6, 4e525dc

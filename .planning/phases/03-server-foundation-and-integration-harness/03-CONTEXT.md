# Phase 3: Server Foundation and Integration Harness - Context

**Gathered:** 2026-11-14
**Status:** Ready for planning

<domain>
## Phase Boundary

The server can be booted by a test, configured without Azure, and every RPC exercised in CI without credentials or a developer's database (requirement TEST-01). Pure infrastructure: no user-facing behavior.

</domain>

<decisions>
## Implementation Decisions

### Claude's Discretion
All implementation choices are at Claude's discretion - pure infrastructure phase. Follow the roadmap implementation note: `public partial class Program`, `Database:Provider`/`Crypto:Provider` read from configuration, `IsDevelopment()` gating diagnostics only (no `#if DEBUG` picking providers), direct Server->Core reference, design-time context factories beside `Persistence/DatabaseBootstrapper`, `Microsoft.AspNetCore.Mvc.Testing` with a test auth handler, SQLite temp files with `ClearAllPools()` by default, InMemory only for pure service logic.

</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets
- `src/AzureDesignStudio.Server.Tests/` already exists (DesignServiceTests, DeployServiceTests, StackRpcTests, WhatIfRpcTests, InProcessTests, ServerPlumbingTests, TestSupport) and calls the gRPC service classes directly with a fake ServerCallContext.

### Established Patterns
- `Server/Program.cs` has `#if DEBUG` blocks (lines ~37, 85, 224) and a `Database:Provider=InMemory` switch (line ~57); `EnsureCreated` runs once at startup (line ~109).
- Package versions are central in `Directory.Packages.props` (no `Microsoft.AspNetCore.Mvc.Testing` entry yet); lock files are checked in locked mode in CI.
- Caller identity comes from `context.UserState[ServiceTools.UserStateKey]`, set by `CallerInterceptor`.

### Integration Points
- `Server/Program.cs`, `Server/Services/HostedServices.cs`, `CryptoService`, `Services/Models/DesignDbContext.cs`, `Server.Tests` csproj + lock file.

</code_context>

<specifics>
## Specific Ideas

No specific requirements - infrastructure phase.

</specifics>

<deferred>
## Deferred Ideas

None
</deferred>

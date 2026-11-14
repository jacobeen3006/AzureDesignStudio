---
phase: "03"
slug: "server-foundation-and-integration-harness"
status: draft
nyquist_compliant: false
wave_0_complete: false
created: "2026-11-14"
---

# Phase 03 - Validation Strategy

> Per-phase validation contract for feedback sampling during execution. Source: 03-RESEARCH.md `## Validation Architecture`.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit 2.9.2 + Microsoft.NET.Test.Sdk 17.12.0 (existing `AzureDesignStudio.Server.Tests`, 134 tests at phase start) |
| **Config file** | none - Wave 0 changes the csproj packages |
| **Quick run command** | `dotnet test src/AzureDesignStudio.Server.Tests --filter "FullyQualifiedName~Hosting"` |
| **Full suite command** | `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false` (CI shape), and the same without `-c Release` |
| **Estimated runtime** | ~60-120 seconds |

---

## Sampling Rate

- **After every task commit:** Run the quick run command
- **After every plan wave:** Run the full suite in Debug and in Release
- **Before `/gsd-verify-work`:** Full suite green in both configurations, both static greps, `dotnet restore src/AzureDesignStudio.Server.Tests --locked-mode`
- **Max feedback latency:** 120 seconds

---

## Per-Task Verification Map

| Req | Behavior | Test Type | Automated Command | File Exists | Status |
|-----|----------|-----------|-------------------|-------------|--------|
| TEST-01 / SC1 | Real `Program` boots with placeholder appsettings, no Key Vault, temp SQLite, Debug and Release | integration | `dotnet test src/AzureDesignStudio.Server.Tests --filter "FullyQualifiedName~ServerHostTests.Boots"` | ❌ W0 | ⬜ pending |
| TEST-01 / SC1 | No `#if` in `Program.cs` | static guard | `! grep -nE '^#(if\|else\|elif)' src/AzureDesignStudio.Server/Program.cs` | ✅ | ⬜ pending |
| TEST-01 / SC2 | Design, Deploy, Cost reachable through the test auth handler; exactly 3 services | integration | `... --filter "FullyQualifiedName~ServerHostTests.EveryServiceIsReachable"` | ❌ W0 | ⬜ pending |
| TEST-01 / SC2 | No credentials -> Unauthenticated; wrong scope -> PermissionDenied; handler fault -> Unknown, host keeps answering | integration | `... --filter "FullyQualifiedName~ServerHostTests.Failure"` | ❌ W0 | ⬜ pending |
| TEST-01 / SC3 | Create design writes a temp `.db`, removed on dispose; no `workspace.db`/`deploy.db` | integration | `... --filter "FullyQualifiedName~ServerHostTests.TempSqlite"` | ❌ W0 | ⬜ pending |
| TEST-01 / SC4 | `Program` reachable as `WebApplicationFactory<Program>` | compile | `dotnet build src/AzureDesignStudio.Server.Tests` | ❌ W0 | ⬜ pending |
| TEST-01 / SC4 | `EnsureCreated` only inside `DatabaseBootstrapper` | static guard | `grep -rn "EnsureCreated" src/AzureDesignStudio.Services src/AzureDesignStudio.Server --include='*.cs'` (single hit) | ✅ | ⬜ pending |
| TEST-01 / design | `DatabaseProvider.Resolve/Configure` rules | unit | `... --filter "FullyQualifiedName~DatabaseProviderTests"` | ❌ W0 | ⬜ pending |
| TEST-01 / design | `DatabaseBootstrapper` on SQLite never runs T-SQL; factory reports SqlServer without connecting | unit | `... --filter "FullyQualifiedName~DatabaseBootstrapperTests"` | ❌ W0 | ⬜ pending |
| TEST-01 / regression | Existing 134 direct-call tests keep passing | unit | full suite command | ✅ | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] Restore unblock: `GHSA-gpwp-5797-mjff` and `GHSA-6vhm-j9h7-f9gh` in `Directory.Build.props` (+ message in `Directory.Build.targets`)
- [ ] `Directory.Packages.props`: Mvc.Testing 10.0.15; Server csproj: Sqlite + Core reference; Server.Tests csproj: Mvc.Testing; commit only Server and Server.Tests lock files
- [ ] `Hosting/AdsServerFactory.cs`, `Hosting/TestAuthHandler.cs`, `Hosting/GrpcTestClients.cs`
- [ ] `Hosting/ServerHostTests.cs`, `Hosting/DatabaseProviderTests.cs`, `Hosting/DatabaseBootstrapperTests.cs`

---

## Manual-Only Verifications

All phase behaviors have automated verification.

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending

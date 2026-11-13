# Whole-Codebase Review

**Date:** 2026-10-28
**Scope:** All 11 projects under `src/` (~64k LOC C#/Razor, 760 source files) + planning artifacts.
**Evidence:** `.planning/codebase/CONCERNS.md` (last mapped commit a05ef8c), `.planning/phases/01-land-and-stabilize/01-REVIEW.md` (49 files, deep), plus fresh verification below.

## Verification run (this session)

- `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` → **Passed: 755, Failed: 0, Skipped: 0** (1s).
- Secret scan over `src/` (excluding bin/obj): no hardcoded credential-shaped strings found.
- `MarkupString`/`innerHTML` usage: only in `TopologyView.razor` (SVG text with `Enc(...)` escaping) and vendor JS (`dom-to-image`, `prism`). The Blazor `MarkupString` in TopologyView interpolates escaped labels — verify `Enc` covers `<`, `>`, `&`, `"`. (Low risk; confirm in code review.)

## Summary of findings

- **High (security):** 2
- **Medium (reliability/maintainability):** 10
- **Low (hygiene):** 6
- **Phase-01 review (separate, 11 findings):** 0 critical, 2 high, 2 medium, 7 low — see `01-REVIEW.md`.

## High

### H1 — Subscription client secrets on the gRPC wire, single RSA key, OAEP size limit
- `deploy_model.proto` `SubscriptionInfo.client_secret`; collected in `AzSubscriptionDrawerTemplate.razor:20`; encrypted at rest with RSA-OAEP against one Key Vault key (`DeployService.cs:76/138`, `CryptoService.cs:37-42`).
- `LoadSubscriptionInfo` decrypts and can return the secret to the client (should never decrypt for display).
- `CryptographyClient.EncryptAsync` fails outright once plaintext exceeds ~190 bytes (RSA-2048 OAEP) — long secrets fail at save time with a generic 400.
- `CryptoService.Decrypt` uses `Encoding.Default` (culture/OS dependent) — should be `Encoding.UTF8`.
- **Fix:** no read path returns the secret; envelope scheme or KV-native reference; explicit UTF-8.

### H2 — No role separation on approval/guardrail/decommission gates
- `DesignService`/`DeployService` guarded only by `[Authorize]` + `[RequiredScope]`. `ApprovePlan` replaces any existing approval for the hash with the caller's own; `SetGuardrailPolicy` writes policy with caller as author; `ApproveDecomWindow`.
- Self-approval is the default workflow; a single compromised account can approve and apply a destructive plan and widen the guardrail that should stop it.
- **Fix:** distinct roles/scopes for request vs. approve vs. guardrail admin; enforce separation server-side.

## Medium

### M1 — Monolithic service/drawer files
- `DeployService.cs` 2,210 lines, 44 catch blocks, 13 RPCs. `TenantScanDrawerTemplate.razor` 2,481 lines, 23 `@inject`s. `CodeDrawerTemplate.razor.cs` 580 lines, `TopMenu.razor.cs` 513 lines.
- **Fix:** split DeployService by RPC family into collaborators; move drawer logic into a testable ViewModel.

### M2 — No central build/package props; version drift
- `Microsoft.AspNetCore.Components.WebAssembly` 10.0.12 vs Server 10.0.13; `Microsoft.Extensions.Logging.Debug` 10.0.0 vs rest 10.0.12. SDK unpinned. `DropForeignThreadingAnalyzers` target copy-pasted in 4 csprojs.
- **Fix:** add `Directory.Packages.props` (CPM), `Directory.Build.targets`, `global.json`.

### M3 — Duplicated ARM resource models
- 447 hand-maintained ARM schema files; `SubResource`, `Sku`, `PrivateEndpoint`, etc. redeclared per service folder. Schema corrections must be applied N times; divergence fails at deploy time, not compile time.
- **Fix:** hoist shared shapes into `AzureResources/Base/`.

### M4 — Two divergent `ads-stencils.json` copies
- `src/AzureDesignStudio/wwwroot/ads-stencils.json` (8,459 B) vs `src/Configurations/ads-stencils.json` (2,467 B) — drifted by 6+ nodes; tests vs. desktop palette mismatch.
- **Fix:** single source under wwwroot, link from K8s ConfigMap/Desktop build.

### M5 — `#if DEBUG` changes infrastructure, not just diagnostics
- `Program.cs:30` InMemory vs SQL Server; `:58` UseSqlServer; `:72` EnsureCreated + hand-written DDL (release only); `:183` gRPC reflection (debug only). Debug and Release are different applications; schema/constraint bugs cannot reproduce in default dev build.
- **Fix:** select provider from config (`Database:Provider`); gate reflection on `IsDevelopment()` only; give devs a relational provider (SQLite/LocalDB).

### M6 — CryptoService throws during DI construction when Key Vault unconfigured
- `CryptoService.cs:19-24`: `new Uri($"https://{configuration["KeyVaultName"]}.vault.azure.net/")` with placeholder `<kv name>` → invalid URI → constructor throws before any RPC body runs. `ICryptoService` registered unconditionally; `DeployService` depends on it.
- **Fix:** lazy vault client; register `NullCryptoService` when `KeyVaultName` is blank.

### M7 — Stencil-load failure silently yields empty palette
- `Desktop/App.xaml.cs:24-31` swallows `catch (Exception)` at startup; no log, no banner.
- **Fix:** log via `ILogger` and surface a diagnostic banner.

### M8 — Device-code sign-in state does not survive app restart
- `MsalAccountService.cs:39-66`: PCA built without token-cache serialization; WAM persistence only covers tokens it issued. Device-code path → anonymous after restart.
- **Fix:** serialize `_pca.UserTokenCache` into `WindowsSecretStore`, rehydrate in constructor.

### M9 — Static `ServiceProvider` installed only after build
- `MauiProgram.cs:26` `Services = null!` until `CreateMauiApp` finishes; `App.xaml.cs:18` consumes it. Startup-ordering risk.
- **Fix:** resolve stencils via injected `AdsContext` instead of static locator.

### M10 — InMemory-in-DEBUG divergence (related to M5)
- Unique indexes, `ExecuteDeleteAsync` semantics, SQL translation bugs cannot reproduce in dev. Covered by M5 fix.

## Low

### L1 — Dead binaries committed at repo root
- `bd2.dll`, `bd_served.dll` (64,277 B each), tracked, referenced by nothing. `git rm` both.

### L2 — `Encoding.Default` in CryptoService.Decrypt (culture/OS dependent)
- Covered under H1.

### L3 — TopologyView uses `MarkupString` with SVG text
- `TopologyView.razor:59/84/85` interpolates `Enc(Short(...))`. Verify `Enc` escapes all five XML entities. (Low risk given escaping helper present.)

### L4 — Duplicated `ads-stencils.json` (Medium above) also a Low hygiene item if not fixed.

### L5 — No `Directory.Build.props` / `global.json` / `nuget.config` (Medium above).

### L6 — Phase-01 code-review findings (11) still open
- 2 High (pointer-drag state machine; unreachable CSS rules), 2 Medium (invalid subnet delegation; case-sensitive vs insensitive subnet name), 7 Low. See `01-REVIEW.md`. None appear fixed yet (no commits after review).

## Recommendations (priority order)

1. **Security (High):** fix H1 (secret read path + encoding + size) and H2 (role separation) before any deploy-capable release.
2. **Dev/prod parity (Medium):** M5/M6/M10 — make Debug use a relational provider and a non-throwing crypto service; this unblocks local testing of deploy paths.
3. **Build reproducibility (Medium):** M2 — centralize package versions and SDK pin.
4. **Data model integrity (Medium):** M3 — hoist shared ARM types.
5. **Palette consistency (Medium):** M4 — single stencils source.
6. **Maintainability (Medium):** M1 — split monoliths.
7. **Hygiene (Low):** L1 — remove dead DLLs; L6 — apply phase-01 review fixes.

## Notes

- No test failures. 755 offline tests green.
- Review report is a synthesis of the existing codebase map + phase-01 review; it is not a line-by-line re-audit of every file. For a targeted deep dive on a specific project/area, request `/gsd-code-review <phase>` or name the files.
- Secrets scan clean; no `eval`/`innerHTML` in first-party Razor except the escaped SVG helper above.
- `CONCERNS.md` last mapped at commit a05ef8c; verify against current HEAD (578949c) if you need freshness — main changes since are phase-01 land commits and phase-02 planning docs.
# CI / Release Pipeline Audit — Supplemental Review

> **Verification correction:** F1's claim that `Encoding.Default` corrupts non-ASCII text on modern .NET is incorrect: on .NET Core and .NET 10, `Encoding.Default` is UTF-8. The suggested encoding mismatch is not a demonstrated bug. Other findings in this supplemental review are candidate findings and require independent verification; the reported HEAD `578949c` below also conflicts with the verified baseline `fa62bb9`.

**Date:** 2026-10-30
**Author:** Subagent CI audit
**Scope:** `.github/workflows/`, `Dockerfile`, `Directory.Build.props`, `Directory.Packages.props`, source project references, release packaging pipeline
**Authority files:** AGENTS.md, ROADMAP.md, REQUIREMENTS.md, CODEBASE-REVIEW.md (2026-10-28)

---

## 1. Stale Prior-Review Claims Verification

Claims from CODEBASE-REVIEW.md audited against current HEAD (578949c):

| Claim | Status | Evidence |
|-------|--------|----------|
| **No SDK pin** (`global.json` missing) | ✅ FIXED | `global.json` present at repo root: `sdk.version: 10.0.401`, `rollForward: latestPatch`, `allowPrerelease: false` |
| **No CPM** (`Directory.Packages.props` missing) | ✅ FIXED | File exists, `ManagePackageVersionsCentrally: true`, 54 `<PackageVersion>` entries covering all transitive deps |
| **Root binaries committed** (bd2.dll, bd_served.dll) | ✅ FIXED | No .dll files at repo root; `git ls-files '*.dll' src/` returns empty |
| **Lock file count mismatch** | ✅ FIXED | 11 tracked csproj ↔ 11 tracked packages.lock.json — one-to-one directory match confirmed |
| **M4: Two divergent ads-stencils.json copies** | ⚠️ STILL VALID | wwwroot = 8,104 B / 50 nodes; Config = 2,361 B / 15 nodes. 35 stencil types only in wwwroot copy (acr, acr, adf, aisearch, aks-baseline...). Consumer paths correct: WASM loads from wwwroot via HttpClient; Desktop links wwwroot copy into output dir; tests read from AppContext.BaseDirectory. Orphaned Config copy never consumed but remains misleading |
| **H1/L2: Encoding.Default in CryptoService.Decrypt** | ⚠️ UNFIXED | Confirmed at `src/AzureDesignStudio.Server/Services/CryptoService.cs:28` — `Encoding.Default.GetString(...)` while Encrypt uses `Encoding.UTF8` (line 33). Asymmetric encoding will corrupt round-trips on non-Windows platforms |
| **M2: DroppedFromThreadingAnalyzers copy-pasted** | PARTIALLY NOTED | Target still present verbatim in 4 csprojs (WASM, Server, Desktop, Core.Tests). No central props solution yet |

---

## 2. Concrete Findings (Ordered by Severity + Impact)

### F1 — CryptoService asymmetry: decrypt reads with `Encoding.Default`, encrypt writes with `UTF-8` [HIGH]

- **File:** `src/AzureDesignStudio.Server/Services/CryptoService.cs:28`
- **Impact:** `Encrypt()` converts plaintext via `Encoding.UTF8.GetBytes()`. `Decrypt()` decodes ciphertext back via `Encoding.Default.GetString()`. On any non-Windows platform (CI Linux runners, Azure container deployments), `Encoding.Default` is ASCII-compatible — multibyte characters (UTF-8 > 0x7F) are replaced with `?`. Subscription secrets containing international characters become irreversibly corrupted after one decrypt cycle. RSA-OAEP size limit (~190 bytes for 2048-bit key) also triggers silently for long secrets.
- **Test:** Deploy a subscription secret containing `üöäé` characters through `Deploy.SaveSubscriptionInfo`; run `CryptoService.Decrypt()` and assert result equals original. Fail on non-Windows runner with different Encoding.Default.
- **GSD Phase:** Phase 2 (build baseline) or Phase 5 (Identity/Guardrails)

### F2 — Build.yml has no `dotnet format` verification step [MEDIUM]

- **File:** `.github/workflows/build.yml:46-58`
- **Requirement missed:** BUILD-03 — "changed file that violates formatting fails the check"
- **Evidence:** build.yml Run steps 46-58 execute restore → build → test with no `dotnet format --verify-no-changes` call. No `.editorconfig` exists in repo (confirmed absent from git index and disk). Without an editorconfig, SDK defaults apply but style conventions (indentation, whitespace trailing, newline discipline) have no machine-enforced gate. PRs can introduce style regressions unblocked.
- **Test:** Submit a PR with a file containing trailing whitespace and mixed tabs/spaces; verify build succeeds (it will pass — confirming the gap).
- **GSD Phase:** Phase 2 Wave 3 ("warnings as errors and the whitespace normalisation")

### F3 — No `TreatWarningsAsErrors` anywhere in repository [MEDIUM]

- **Files:** All 11 csprojs (none set it); `Directory.Build.props` contains only `MauiVersion` and `RestorePackagesWithLockFile`
- **Requirement missed:** BUILD-03 — "app projects build with warnings as errors"
- **Impact:** New compiler warnings slip into main unblocked (nullability gaps, unused imports, deprecated APIs). Builds appear green despite accumulating technical debt. Only two NU1903 suppressions exist (AutoMapper DoS allowlist) in WASM and Core.Tests csprojs.
- **Test:** Add `// TODO: unused variable` to any source file and build; warning prints but exit code 0 confirms no WAE.
- **GSD Phase:** Phase 2 Wave 3

### F4 — CodeQL actions stale across three workflow definitions [LOW-MEDIUM]

- **Files & lines:**
  - `.github/workflows/codeql-analysis.yml:43`: `actions/checkout@v3` (v4 required, security advisory on v3)
  - `.github/workflows/codeql-analysis.yml:49`: `codeql-action/init@v2` (v3 required)
  - `.github/workflows/codeql-analysis.yml:79`: `codeql-action/analyze@v2` (v3 required)
  - `.github/workflows/build.yml:85`: `codeql-action/init@v3` ✅ current
  - `.github/workflows/build.yml:93`: `codeql-action/analyze@v3` ✅ current
- **Impact:** `codeql-analysis.yml` runs monthly (schedule cron) and on every PR — outdated actions may use deprecated API endpoints or lack CVE query pack updates. The `init@v2` → `analyze@v2` pair skips newer codebase-aware analysis built into v3 autobuild path.
- **Test:** Verify GitHub UI shows action version deprecation notices on the monthly schedule runs.
- **GSD Phase:** Phase 2 (general hygiene)

### F5 — Stencil catalog drift: Config/ads-stencils.json has 15 nodes, wwwroot/ads-stencils.json has 50 [MEDIUM]

- **Files:** `src/Configurations/ads-stencils.json` (2,361 B, 15 items) vs `src/AzureDesignStudio/wwwroot/ads-stencils.json` (8,104 B, 50 items). 35 service types (aci, acr, adf, aks-baseline, appservice, automation, cognitive, container, cosmosdb, eventgrid, eventhub, keyvault, logicapps, mysql, postgresql, redis, sql, storage, synapse, ... ) exist only in wwwroot copy.
- **Impact:** The orphaned `src/Configurations/` copy serves zero consumers but misleads maintainers into thinking it's canonical. If someone later edits the Config copy expecting it to propagate to production, changes vanish silently. Desktop correctly links the wwwroot file via MSBuild `<Content Include>` link, but the dual-file pattern invites future drift.
- **Test:** `git show HEAD:src/Configurations/ads-stencils.json` vs `git show HEAD:src/AzureDesignStudio/wwwroot/ads-stencils.json` — compare JSON node counts and sets programatically. Assert `len(config_nodes) == len(wwwroot_nodes)`.
- **GSD Phase:** Phase 2 or Phase 8 (cleanup)

### F6 — AutoMapper NU1903 suppression without expiry [MEDIUM]

- **Files:** `src/AzureDesignStudio/AzureDesignStudio.csproj:21` and `src/AzureDesignStudio.Core/AzureDesignStudio.Core.csproj:12` — both set `<NoWarn>$(NoWarn);NU1903</NoWarn>`
- **Comment at line 19 in WASM csproj explains rationale:** "AutoMapper 12.0.1 GHSA-rvv3-g6hj-g44x (DoS via uncontrolled recursion)"
- **ROADMAP claim:** Phase 2 Wave 4 requires "High/Critical audit gate with the single dated AutoMapper exception". Phase 8 implementation note states: "AutoMapper replaced through existing Roslyn/Scriban generator with goldens unchanged and a trimmed-publish boot job, which retires Phase 2's dated allowlist entry."
- **Impact:** The allowlist has no expiry date. When AutoMapper 13+ becomes licensable or the source-generator replacement lands in Phase 8, this suppression persists indefinitely, hiding future vulnerable versions.
- **Test:** Update AutoMapper to 13.0.0 in Directory.Packages.props; confirm build continues suppressing NU1903 rather than failing.
- **GSD Phase:** Phase 2 Wave 4 (add dated expiry) → Phase 8 (remove after source-gen migration)

### F7 — publish.yml release artifact unsigned when MSIX signing fails silently [LOW]

- **File:** `.github/workflows/publish.yml:87-100`
- **Issue:** Sign step runs `if: env.MSIX_PFX != ''`. If PFX secret is absent/unset, the sign step is skipped entirely, and the unsigned `.msix`/`.appx` is still uploaded and included in the GitHub release. Phase 11 SIGN-01 requires: "tagged release fails when signing material is absent; only manual dispatch may explicitly request an unsigned build."
- **Impact:** A tagged push without PFX secrets produces an officially published unsigned package that users must manually trust. A manual workflow_dispatch without proper gating does the same. There is no conditional "fail if unsigned" for tag-triggered pushes.
- **Test:** Push a `v0.0.1-test` tag with no `MSIX_PFX_BASE64` secret configured; verify MSIX artifact uploads signed — it currently won't fail.
- **GSD Phase:** Phase 11 Wave 1

### F8 — Dockerfile base images unpinned [LOW]

- **File:** `src/AzureDesignStudio.Server/Dockerfile:3,8`
- **Lines:** `FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base` and `FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build`
- **Impact:** Tag `10.0` rolls forward automatically. Different builds at different times pull different patch levels. Global.json pins the CLI SDK to 10.0.401 but Dockerfile COPY/restore runs before global.json resolution in the build stage; the runtime image (base:10.0) could be newer or older at deployment time. Non-reproducible container builds.
- **Test:** Remove pinned lock files temporarily and rebuild Docker image twice with hours between — diff the resulting image layers.
- **GSD Phase:** Phase 2

---

## 3. Already-Planned Gaps (Not New, Just Verified)

| Finding | Roadmap Reference | Current State |
|---------|-------------------|---------------|
| BUILD-03: format check + warnings-as-errors | Phase 2 Waves 3-4 | Not implemented (findings F2, F3) |
| BUILD-04: dated auto mapper allowlist | Phase 2 Wave 4 | NU1903 suppressed without expiry (finding F6) |
| BUILD-05: Windows Desktop blocking | Phase 2 Waves 5-7 | Desktop not in Ubuntu build job scope |
| SHELL-05: Doc registry link check | Phase 7 | No CI link-checker step |
| SIGN-01: Fail on absent signing | Phase 11 | publish.yml allows unsigned upload (finding F7) |
| SIGN-04: CycloneDX SBOM + checksums | Phase 11 | Not present in publish.yml |
| TEST-05: Test count assertions | Phase 8 | CI has no `--glb` or count assertion |

These are already planned in the roadmap but not yet implemented — they are not new findings, just independently verified gaps against the current code state.

---

## 4. Summary

**Top 5 actionable findings:**

1. **F1 — Encoding.Default in CryptoService decrypt:** Symmetric encryption/decoding mismatch corrupts non-ASCII subscription secrets outside Windows. Fix: change line 28 from `Encoding.Default` to `Encoding.UTF8`.
2. **F3 — No TreatWarningsAsErrors:** Zero WAE configuration; 11 projects accumulate unchecked warnings silently. Fix: add `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` to `Directory.Build.props`.
3. **F2 — No format verification in CI:** BUILD-03 format check completely absent from build.yml. Fix: add `dotnet format --verify-no-changes --verbosity quiet` step after build.
4. **F5 — Drifted stencil copy:** 35 services only in wwwroot; orphaned Config/ copy is dead weight. Fix: delete `src/Configurations/ads-stencils.json` and link from K8s configmap per roadmap M4 recommendation.
5. **F6 — No-expiry AutoMapper allowlist:** NU1903 suppressed with explanation comment but no expiry date. Fix: add dated comment or use `__skip_version_check` metadata pattern, and commit removal in Phase 8 after Roslyn/Scriban generator lands.

**Pipeline integrity score:** 6/10
- Lock files: ✅ complete and matched (11↔11)
- SDK pin: ✅ global.json at 10.0.401/latestPatch
- CPM: ✅ Directory.Packages.props with all versions
- Restore locked mode: ✅ in build.yml
- Missing format gate: ❌ (BUILD-03)
- Missing WAE: ❌ (BUILD-03)
- Unsigned MSIX upload possible: ❌ (SIGN-01)
- Unpinned Docker base images: ❌
- CodeQL action staleness: ⚠️
- Expired/missing SBOM & checksums: ❌ (SIGN-04)
- Encoding bug still live: ❌ (F1)

---

*This review is read-only source analysis only. No source files were modified.*

---
phase: 05-identity-guardrails-and-offline-development
plan: 05
subsystem: auth
tags: [crypto, data-protection, key-vault, offline-development, launch-settings, xunit]

requires:
  - phase: 03-server-foundation-and-integration-harness
    provides: "ICryptoService seam, CryptoProvider with the one-value KeyVault switch, AdsServerFactory host harness (03-03)"
  - phase: 05-identity-guardrails-and-offline-development
    provides: "Typed caller on the Deploy RPCs and the hosted-service layout the SaveSubscriptionInfo path runs on (05-02, 05-03)"
provides:
  - "DevelopmentCryptoService: ICryptoService over ASP.NET Core Data Protection, ciphertext prefixed dev1:, purpose AzureDesignStudio.SubscriptionSecret.v1"
  - "CryptoProvider.Register(services, configuration, environment): Crypto:Provider = Development (case-insensitive), Crypto:DevelopmentKeyRingPath override, Production refusal"
  - "Key ring folder created on first use: mode 0700 on Unix, DPAPI-protected keys on Windows"
  - "Crypto__Provider=Development on the AzureDesignStudio.Server and IIS Express launch profiles only"
  - "DevelopmentCryptoTests (host level) and an extended CryptoProviderTests"
affects: [05-10, 05-11]

actuals:
  tokens: 7000
  tasks: 2
  commits: 2

tech-stack:
  added: []
  patterns:
    - "Standalone DataProtectionProvider.Create instead of AddDataProtection, so the development key ring never becomes the application-wide key repository (antiforgery, cookies)"
    - "Provider chosen by configuration plus IHostEnvironment, with runtime OperatingSystem checks, never a preprocessor conditional in Program.cs"
    - "Ciphertext prefix (dev1:) that is not valid base64, so each crypto service fails into the existing broken-link 404 path on the other's output"

key-files:
  created:
    - src/AzureDesignStudio.Server/Services/DevelopmentCryptoService.cs
    - src/AzureDesignStudio.Server.Tests/Hosting/DevelopmentCryptoTests.cs
  modified:
    - src/AzureDesignStudio.Server/Services/CryptoProvider.cs
    - src/AzureDesignStudio.Server/Program.cs
    - src/AzureDesignStudio.Server/Properties/launchSettings.json
    - src/AzureDesignStudio.Server.Tests/Hosting/CryptoProviderTests.cs

key-decisions:
  - "The development key-ring folder is created lazily inside the singleton factory (first use), not at Register time, so registering the provider has no filesystem effect and a Production refusal leaves no folder behind"
  - "A key-ring folder that already exists is left untouched (no chmod): only a folder this code creates is forced to 0700, because a configured path may be a shared or pre-existing directory"
  - "No extra tripwire refusing the development provider when a real vault name is configured (research assumption A7); the Production refusal is the required control"
  - "Cross-provider blobs are not migrated: a vault blob read by the development service, or the reverse, is an unreadable link that answers 404 and is fixed by saving the subscription again"

patterns-established:
  - "Launch-profile and checked-in-settings guard test: every profile that sets Crypto__Provider=Development must also set ASPNETCORE_ENVIRONMENT=Development; Docker, appsettings*.json and the Dockerfile never select it"

requirements-completed: [CRED-01, CRED-02]

coverage:
  - id: D1
    description: "A host with Crypto:Provider=Development registers a subscription (200), lists it, stores a dev1:-prefixed secret that differs from the plaintext, and the host's ICryptoService decrypts it back, with no Key Vault name configured and no Azure credential"
    requirement: "CRED-01"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/DevelopmentCryptoTests.cs#TracerDevelopmentProviderStoresListsAndDecryptsWithNoVaultAndNoCredential"
        status: pass
    human_judgment: false
  - id: D2
    description: "Production refuses development keys: Register throws (naming Production and KeyVault) before registering anything, and a Production host with Crypto:Provider=Development does not start; Production with KeyVault and no vault still boots and SaveSubscriptionInfo answers 503 in-band"
    requirement: "CRED-02"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/CryptoProviderTests.cs#DevelopmentInProductionIsRefusedBeforeAnythingIsRegistered"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/DevelopmentCryptoTests.cs#ProductionHostWithTheDevelopmentProviderDoesNotStart"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/DevelopmentCryptoTests.cs#ProductionHostWithKeyVaultAndNoVaultBootsAndAnswers503InBand"
        status: pass
    human_judgment: false
  - id: D3
    description: "The dev1: prefix isolates development ciphertext: it is never valid base64 (the vault service fails with FormatException), the development service refuses unprefixed and tampered payloads with CryptographicException, and an unreadable development blob turns the link into a 404"
    requirement: "CRED-01"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/DevelopmentCryptoTests.cs#DevelopmentCiphertextIsPrefixedAndNeverValidBase64"
        status: pass
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/DevelopmentCryptoTests.cs#DevelopmentServiceRefusesUnprefixedAndTamperedPayloads"
        status: pass
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/DevelopmentCryptoTests.cs#ADevelopmentBlobThatIsUnreadableMakesTheLinkA404"
        status: pass
    human_judgment: false
  - id: D4
    description: "The key ring is created on first use and is user-only (0700) on Unix; on Windows the keys are DPAPI-protected (code path only, not run on this macOS machine)"
    requirement: "CRED-01"
    verification:
      - kind: integration
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/DevelopmentCryptoTests.cs#KeyRingDirectoryIsCreatedOnFirstUseAndIsUserOnlyOnUnix"
        status: pass
    human_judgment: true
    rationale: "The Windows DPAPI branch (ProtectKeysWithDpapi behind OperatingSystem.IsWindows()) compiles everywhere but only executes on Windows; it was not exercised here. The mode assertion is skipped on Windows by design."
  - id: D5
    description: "Only the Development launch profiles enable the development provider; the Docker profile, the checked-in appsettings files and the Dockerfile never do"
    requirement: "CRED-02"
    verification:
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/DevelopmentCryptoTests.cs#OnlyDevelopmentLaunchProfilesEnableTheDevelopmentProvider"
        status: pass
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/DevelopmentCryptoTests.cs#CheckedInSettingsNeverSelectTheDevelopmentProvider"
        status: pass
      - kind: unit
        ref: "src/AzureDesignStudio.Server.Tests/Hosting/DevelopmentCryptoTests.cs#TheDockerfileNeverSelectsTheDevelopmentProvider"
        status: pass
    human_judgment: false

duration: 25min
completed: 2026-11-14
status: complete
plan_head_before: d25a870dfd04c89f6ea910edd4ba11cac36499f6
plan_head_after: f8bf081e4492160c2af86ee18a79267b49836005
commits: 2
---

# Phase 5 Plan 05: Development Crypto Provider and Production Refusal Summary

**A developer can run the server offline with `Crypto__Provider=Development` (Data Protection, file key ring, `dev1:`-prefixed secrets, no vault and no Azure credential), while a Production host refuses to start on development keys and a Production KeyVault host without a vault still boots and answers 503.**

## Performance

- **Duration:** about 25 min
- **Tasks:** 2 (1 tracer, 1 TDD)
- **Files:** 6 (2 created, 4 modified); no package or lock file changed

## Accomplishments

- Task 1 (tracer): `DevelopmentCryptoService` wraps an `IDataProtector` (purpose `AzureDesignStudio.SubscriptionSecret.v1`), prefixing output with `dev1:` and refusing anything else with `CryptographicException`, which `DeployService.GetSubscriptionInfo` already maps to a 404 "link again". `CryptoProvider.Register` gained the `IHostEnvironment` parameter, the `Development` value and `Crypto:DevelopmentKeyRingPath`; the provider is built standalone with `DataProtectionProvider.Create` (application name `AzureDesignStudio.Dev`). The host-level tracer registers, lists and decrypts a subscription on a factory host with no vault name configured.
- Task 2 (TDD, RED then GREEN): `Register` throws `InvalidOperationException` ("...never allowed in the Production environment. Use KeyVault.") as the first thing the Development branch does, so nothing is registered; the key-ring folder is created on first use (0700 explicitly on Unix, `ProtectKeysWithDpapi` behind `OperatingSystem.IsWindows()`); `Crypto__Provider=Development` was added to the `AzureDesignStudio.Server` and `IIS Express` profiles only.
- Guard tests read `launchSettings.json`, `appsettings*.json` and the Dockerfile from the repository root (walk up to `global.json`).

## Task Commits

1. **Task 1: development crypto provider on Data Protection** - `44b10b5` (feat)
2. **Task 2: Production refuses the development crypto provider; launch profiles enable it for Development only** - `f8bf081` (feat)

Tracer gate (Auto/default end-of-phase mode, automated-only verify) was re-run end to end before expanding: 32 passed, 0 failed. Task 2's tests were written first; the four new facts that exercise the refusal, directory creation and launch profiles failed (RED) before the implementation and passed afterwards (GREEN). The plan prescribes one commit per task, so RED and GREEN share the Task 2 commit.

## Decisions Made

See `key-decisions` above.

## Deviations from Plan

None - plan executed exactly as written. Two small additions inside the plan's stated scope: tests for `KeyVaultIsStillAllowedInProduction` and `ADevelopmentBlobThatIsUnreadableMakesTheLinkA404` (the latter proves the broken-link path end to end through `GetResourceGroups`), and a Dockerfile guard beside the appsettings guard.

## Verification

- Task 1 filter (DevelopmentCryptoTests, CryptoProviderTests, SourceGuardTests, ServerHostTests): 32 passed, 0 failed.
- Task 2 filter (DevelopmentCryptoTests, CryptoProviderTests): 28 passed, 0 failed.
- Full suite in the CI shape, `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false`: 362 passed, 0 failed, 9 skipped (the Category=SqlServer facts, skipped by design without `ADS_TEST_SQLSERVER`).
- Mutation check required by the plan: with the Production refusal disabled (`if (false && environment.IsProduction())`), `DevelopmentInProductionIsRefusedBeforeAnythingIsRegistered` and `ProductionHostWithTheDevelopmentProviderDoesNotStart` failed; the change was reverted and the file matches the committed version.
- Acceptance criteria: `dev1:` in `DevelopmentCryptoService.cs` = 1 line (>=1); `builder.Environment` in `Program.cs` = 3 (>=3); `Crypto__Provider` in `launchSettings.json` = 2; `Crypto` in `appsettings.json` = 0; `SourceGuardTests` passes; `git diff --stat HEAD -- '*.csproj' src/Directory.Packages.props` empty; `git status --porcelain -- '*packages.lock.json'` empty (no SDK drift appeared).
- Not exercised on this machine: the Windows DPAPI branch (see D4 `human_judgment`). No real server was launched and no key ring was written outside temp directories (every test uses a fresh folder under the system temp path and deletes it).

## Issues Encountered

None.

## Known Stubs

None.

## Threat Flags

None. The surface added (a file key ring under the developer profile, a configuration switch) is exactly what T-05-18 to T-05-21 cover: T-05-18 and T-05-21 mitigated by the Production refusal plus the launch-profile/appsettings/Dockerfile guards, T-05-19 mitigated by 0700/DPAPI (macOS and Linux keys remain plaintext XML in a user-only folder, an accepted residual that plan 05-10's operator docs must state), T-05-20 mitigated by the `dev1:` prefix.

## Next Phase Readiness

Ready for the next plan. Plan 05-10's operator documentation should state the macOS/Linux plaintext-at-rest residual, the `Crypto:DevelopmentKeyRingPath` override, and that switching providers makes existing linked subscriptions unreadable until they are linked again.

## Self-Check: PASSED

- FOUND: src/AzureDesignStudio.Server/Services/DevelopmentCryptoService.cs
- FOUND: src/AzureDesignStudio.Server.Tests/Hosting/DevelopmentCryptoTests.cs
- FOUND commits: 44b10b5, f8bf081

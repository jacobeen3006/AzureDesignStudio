# Pitfalls Research

**Domain:** Subsequent milestone on an existing .NET 10 Blazor WASM + MAUI Blazor Hybrid + gRPC + SQLite desktop product (Azure Design Studio)
**Researched:** 2026-10-21
**Confidence:** HIGH on platform mechanics (Microsoft/SQLite primary docs); MEDIUM on CI-timing and signing-in-practice claims; repo findings are code-verified and marked as such.

**Scope note.** This is not greenfield research. Everything below assumes the shipped v1/v2 system (`src/`, 11 projects, 755 offline tests) and asks: *what breaks when we add the Trust Gate items, the Round 2 shell, and a signed MSIX to a product that already has users, a live `workspace.db`, and a two-replica server?*

## Phase key used throughout

Taken from `docs/next-phases-plan.md` §3 (the milestone's execution authority), with the v3 item IDs it implements.

| Phase | Content | v3 items |
|---|---|---|
| **P1** | Land and subtract: commit the uncommitted slice, remove root blobs, untrack scratch, push | — |
| **P2** | Lifetime bugs + docs truth: pointer zone, command registry owner, docs counts | N50 partial |
| **P3** | Baseline gates: `global.json`, non-blocking Windows Desktop job, vulnerable-package gate, `dotnet format` | N1 (staged) |
| **P4** | Trust-Gate smalls: identity, local crypto, limits/health, payload contract, guardrail scoping, migrations + WAL | N11, N12, N14, N26, N10, N17+N18 |
| **P5** | Verification depth: server integration tests, committed E2E, bUnit, coverage budget, MCP contract | N3, N2, N4, N5, N6 |
| **P6** | Trust-Gate tail: AutoMapper removal, build policy, CSP + supply chain + release signing, credential model, blocking Windows job | N46, N47, N13, N9, N1 |
| **P7** | Capability work; Round 2 track runs in parallel but surfaces only after G6 | — |

---

## Critical Pitfalls

### Pitfall 1: The MSIX package identity is decided in three places that already disagree

**What goes wrong:**
The package installs on the signing machine and fails on a clean one, or fails at signing with `0x8007000B`, or updates replace the app instead of upgrading it. The repo currently carries three independent sources of identity and version:

- `src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj`: `ApplicationId=com.azuredesignstudio.desktop`, `ApplicationDisplayVersion=1.0`, `ApplicationVersion=1`, **`WindowsPackageType=None`**;
- `src/AzureDesignStudio.Desktop/Platforms/Windows/Package.appxmanifest`: `Identity Name="AzureDesignStudio.Desktop" Publisher="CN=Azure Design Studio" Version="1.0.0.0"`;
- `.github/workflows/publish.yml`: `-p:WindowsPackageType=MSIX -p:RuntimeIdentifier=win-x64 -p:WindowsAppSDKSelfContained=true -p:SelfContained=true`, then a `Package.appxmanifest` version stamp via
  `-replace 'Version="\d+\.\d+\.\d+\.\d+"', "Version=`"$v.0`""`.

Three failure modes follow. (a) **Publisher mismatch**: Microsoft documents that the manifest `Publisher` must match the signing certificate's subject *exactly, including all distinguished-name fields in the same order*; otherwise SignTool fails `0x8007000B` (Event 150). `CN=Azure Design Studio` is a placeholder that will not match a real CA certificate (`CN=..., O=..., C=...`). (b) **Version**: the last (fourth) section of the package version is reserved and Microsoft instructs that it "must be left as 0 when you build your package" (stated for Store submission; keep it as the default for any release), and the first section cannot be 0 — a tag like `v3.1.0` becomes `3.1.0.0` (fine), but `v3.1` becomes `3.1.0` and the regex leaves the manifest with a 3-part version, and any `v1.2.3.4` tag produces a five-part string the schema rejects. Note also that this milestone distributes by **sideload**: the Store's automatic re-signing does not apply, so the package must be signed by us with a certificate whose subject we control. (c) **`WindowsPackageType`**: Microsoft explicitly calls out that for Blazor Hybrid "the default may not produce an MSIX package" and that the property must be set explicitly; setting it *only* on the command line means a plain `dotnet publish` (and any future VS publish or TFM change) silently produces an unpackaged app. The csproj says `None` while CI says `MSIX` — the two disagree by construction.

**Why it happens:**
MSIX identity is a tuple (Name, Publisher, Version) spread across csproj, manifest, certificate, and command line. Everyone edits the one file in front of them. The manifest is also still the MAUI template file (`$placeholder$.png`, `CN=Azure Design Studio`), which looks plausible, so nobody re-reads it.

**How to avoid:**
1. Move the packaging properties into the csproj, conditioned exactly like the official sample:
   `Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'windows' and '$(Configuration)' == 'Release'"` → `WindowsPackageType=Package`, `AppxPackageSigningEnabled=true`.
2. Decide one authority for `Publisher` (the certificate's `Subject` string, pasted verbatim) and one for `Name`, then add a CI assertion step that reads `Get-AppxPackageManifest` / `makeappx unpack` output and compares both against `$(ApplicationId)` and a checked-in expected publisher. Fail the build, not the install.
3. Stamp the version from `src/version.json` (Nerdbank.GitVersioning already produces `Major.Minor.Patch.Build`) into the manifest through MSBuild property replacement (`ApplicationDisplayVersion`/`ApplicationVersion`), not a YAML regex. Assert the result matches `^\d+\.\d+\.\d+\.0$`.
4. Pass `-p:RuntimeIdentifierOverride=win-x64` (per the .NET 10 MAUI publish doc) *in addition to* `-p:RuntimeIdentifier=win-x64`; the docs cite WindowsAppSDK issue #3337 as the reason this second property group exists, and the current workflow uses only the first.
5. Signing trust is a **machine**-store property: `Import-Certificate -CertStoreLocation Cert:\LocalMachine\TrustedPeople`. Installing into the user store (the natural instinct when you double-click a `.cer`) leaves App Installer showing "Publisher: unknown" even though the package is validly signed.

**Warning signs:**
`signtool` returns `0x8007000B`; Event Viewer `AppxPackagingOM` Event 150; the "Locate package" step becomes ambiguous because `Get-ChildItem -Include *.msix,*.appx` also matches the old `.appx` from a prior unpackaged build; `Get-AppxPackage | Select Name` shows a name that matches neither csproj nor manifest; installing twice creates two entries instead of upgrading.

**Phase to address:**
**P3** (the Windows Desktop job must produce the package template, so the identity question is forced early) and **P6** (release signing is part of N13's supply-chain scope). Do not leave this to the ship week — the certificate's exact subject string is a procurement dependency.

---

### Pitfall 2: The Windows job is expensive on every run, so it gets made "non-blocking" forever

**What goes wrong:**
`dotnet workload install maui-windows` runs on every job invocation with no cache. GitHub's `windows-latest` images do not pre-install the MAUI workloads — the open runner-images request (#6936) records maintainers declining to pre-install, with build-time installation measured at "8min+ on windows agents" (a 2025 comment: ~2 min; a StackOverflow report: 5–10 min in Azure DevOps). The local measurement in `docs/assessment-2026-10-21.md` §6 is worse: a Release Desktop build was still compiling after **11+ minutes with a warm local cache**. Add NuGet restore and packaging and a single PR check can approach the 20-minute acceptance budget in v3 N1 with nothing left for the actual build. The predictable reaction is to keep `continue-on-error: true` — and then the "Desktop builds on PRs" requirement is satisfied on paper only.

**Why it happens:**
MAUI workload install is a network-bound, per-SDK-band operation; caching is not in the template; and the first-person cost estimate comes from a cold machine.

**How to avoid:**
- Cache precisely: `$HOME/.dotnet` is *not* what you want; cache the workload packs under the SDK install root or use `actions/cache` on `~/.dotnet` + `%USERPROFILE%\.dotnet\metadata` keyed by `global.json`'s SDK band + `dotnet --version` + the `$(MauiVersion)` manifest hash. Restore with `dotnet workload restore src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj` (project-scoped, not `install maui`, which pulls android/ios/tizen that this repo never uses).
- Do the workload install/pack in a **separate job** from the build so a cache hit skips it entirely, and publish the workload set as a cached artifact.
- Split the gate: a fast `dotnet build -c Release` (no packaging) on every PR, and `-p:WindowsPackageType=MSIX` only on the packaging job (nightly + tags). Packaging adds SDK tooling and appx generation on top of an already-expensive build.
- Budget with evidence: record the duration in `docs/next-phases-plan.md` (P3 already asks for this) and flip `continue-on-error` off only after two consecutive green runs.

**Warning signs:**
Job duration drifts up without a code change; "Install MAUI workload" dominates the step timeline; the cache key includes no SDK version so a runner image bump silently invalidates it; PRs queue behind a 20-minute job and people start pushing without waiting.

**Phase to address:**
**P3** (non-blocking job first, with duration recorded), **P6** (make it blocking).

---

### Pitfall 3: Replacing `EnsureCreated` with migrations produces a schema with no migration history

**What goes wrong:**
This is the single most likely data-loss path in the milestone, because five call sites must change together and three of them are on the RPC hot path:

- `src/AzureDesignStudio.Server/Program.cs:83` — `db.Database.EnsureCreated()` in the `#if !DEBUG` bootstrap, followed by ~60 lines of hand-written idempotent T-SQL that creates the same five audit tables the EF model already declares (`AdsPlanApprovals`, `AdsPlanApplyEvents`, `AdsGuardrailPolicies`, `AdsDecomApprovals`, `AdsDecomWindowEvents`);
- `src/AzureDesignStudio.Server/Services/DesignService.cs:20` and `DeployService.cs:40` — `EnsureCreated()` **in the service constructors**, so it runs per RPC;
- `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs:91` — `EnsureCreated()` on the desktop file.

The order of operations decides whether the database survives. `EnsureCreated` creates the schema **without** `__EFMigrationsHistory`; a later `Database.Migrate()` then tries to apply `InitialCreate` against tables that already exist and fails ("There is already an object named 'AdsDesigns' in the database"). Microsoft states this directly: "EnsureCreatedAsync and Migrations don't work well together… Transitioning from EnsureCreatedAsync to Migrations is not a seamless experience… The simplest way to do it is to drop the database and re-create it" — which is exactly what you cannot do to users' `workspace.db` files.

On SQLite there are three extra traps: idempotent migration scripts are **not supported** (no procedural language), so "generate a script and re-run it safely" is off the table; several operations (unique constraints, column alters, PK changes) are implemented as **table rebuilds** and throw `NotSupportedException` when the artifact is not part of the EF model — which is exactly the status of the tables `EnsureAdditiveTables()` created by raw SQL; and EF 9+ takes a `__EFMigrationsLock` row that is **not released if the process is killed mid-migration**, after which every subsequent migration waits indefinitely until `__EFMigrationsLock` is dropped by hand.

**Why it happens:**
`EnsureCreated` is the path of least resistance during prototyping, and it keeps working as long as the schema only grows. The additive `ALTER TABLE`/`CREATE TABLE IF NOT EXISTS` shim in `WorkspaceDb` masks the problem for two years, so the migration feels like a mechanical swap rather than a data migration.

**How to avoid:**
1. **Delete the three redundant `EnsureCreated()` calls first** (`DesignService.cs:20`, `DeployService.cs:40`, and the one in `Program.cs` after the bootstrap is replaced). A per-RPC schema check that races the migration is the worst possible interleaving.
2. **Baseline, don't re-create.** For both providers: generate `InitialCreate` from the *current* model, then detect an existing database (tables present, no history table), verify the schema matches the model, and insert the baseline row into `__EFMigrationsHistory` (`20261021_InitialCreate`). Only fresh files get `Migrate()` from zero. `WorkspaceDb` needs a `CreateContext` that no longer bootstraps.
3. Keep the raw DDL and the EF model in lockstep **only until** the baseline row is written; after that the DDL block must be deleted, or the two will drift and the next migration will disagree with production. Today they already disagree in detail: the DDL declares `PlanHash nvarchar(71)` PK and `Id uniqueidentifier` PK for decom events, while the model uses `[StringLength(71)]` + `[Key]` and an `Id` Guid — verify with a real `Update-Database --script` diff before baselining.
4. For SQLite, ship a **backup before migrate** (`VACUUM INTO` to a timestamped file — see Pitfall 5), run `PRAGMA quick_check` after, and treat a non-`ok` result as a hard stop that restores the backup and leaves the old binary runnable.
5. Give the desktop an upgrade fixture: a checked-in `workspace.v1.db` / `workspace.v2.db` produced by the shipped binaries, with row counts asserted before and after migration. `.planning/codebase/CONCERNS.md` already flags this as the highest-risk untested path.

**Warning signs:**
`Migrate()` marked as a success but `SELECT * FROM __EFMigrationsHistory` is empty; a fresh developer database and a migrated one have different `INDEXPROPERTY`/`pragma_index_list` output; migration tests pass against `:memory:` but fail against a copied real file; a migration runs "instantly" on a database that should have rebuilt a table; `dotnet ef database update` hangs (abandoned `__EFMigrationsLock`).

**Phase to address:**
**P4** (4.6 "N17 + N18 together" — the plan already insists migrations and WAL land together; this pitfall is why). **P5** adds the upgrade-fixture test to the server/workspace suite.

---

### Pitfall 4: The baseline migration fails on data the old schema allowed

**What goes wrong:**
`WorkspaceDbContext.OnModelCreating` declares constraints that the shipped additive DDL never created:

- `model.Entity<DesignRecord>().HasIndex(d => d.Name).IsUnique()`;
- `model.Entity<ChangeFeedRecord>().HasIndex(c => new { c.TargetResourceId, c.ChangeKey }).IsUnique()`.

But `WorkspaceDb.EnsureAdditiveTables()` creates `ChangeFeed` with a plain `PRIMARY KEY` on `Id` and **no unique index**, and `EnsureCreated()` is a no-op on an existing file. So every real workspace in the field has *weaker* constraints than the model promises. The baseline migration will try to create those unique indexes; if a duplicate `(TargetResourceId, ChangeKey)` row exists (entirely possible — the code has no protection), `CREATE UNIQUE INDEX` fails, the migration aborts midway, and the user is left with a half-migrated file. The same class of failure applies to the server's `ADS_CHECK_JSON` check constraint on `AdsDesigns` (`ISJSON(DesignData)=1`): a row written before the constraint existed with non-JSON body blocks the constraint creation.

**Why it happens:**
`EnsureCreated` + additive DDL means the model is aspirational, not authoritative. Nobody has ever compared `pragma_index_list` on a field database to `OnModelCreating`.

**How to avoid:**
- Before writing the migration, dump the *actual* schema of a real file and diff it against the model: `SELECT type,name,tbl_name,sql FROM sqlite_master` on a copy of a user `workspace.db`; `sp_helpindex`/`OBJECT_ID` checks on the server DB.
- In the baseline migration, **repair data before adding the constraint**: dedupe `ChangeFeed` by `(TargetResourceId, ChangeKey)` keeping the newest `ChangeTime`, log the count removed, and only then create the index. Do it in an explicit `migrationBuilder.Sql(...)` block with a comment stating why.
- Add a pre-flight query to the migration path that counts violating rows and refuses to migrate (with a user-visible message + backup path) instead of throwing mid-transaction.
- Treat "model declares an index" as unproven until a test asserts it on a migrated file.

**Warning signs:**
The upgrade fixture passes on a hand-built DB but fails on a copied user file; `SQLITE_CONSTRAINT` / "UNIQUE constraint failed" during migration; a `ChangeFeed` query that used to return duplicates suddenly throws; migrations that pass locally because the developer's file was recreated recently.

**Phase to address:**
**P4** (4.6), verified by **P5** fixtures.

---

### Pitfall 5: WAL and the read-only MCP consumer are mutually exclusive as currently configured

**What goes wrong:**
Three independent settings interact, and the current code picks the losing combination at every point:

- `WorkspaceDb` (desktop writer): `DataSource + Cache=Shared`, no journal mode, no busy timeout (`WorkspaceDbContext.cs:80-89`).
- `AdsWorkspace` (MCP reader): `DataSource + Mode=ReadOnly + Cache=Shared` (`src/AzureDesignStudio.Mcp/AdsWorkspace.cs:20-25`).
- `WorkspaceDb` mutates schema at startup (`ALTER TABLE`, `CREATE TABLE IF NOT EXISTS`), which needs an exclusive lock.

Adding `journal_mode=WAL` (N18) changes the rules:

1. **Shared cache + WAL is explicitly discouraged.** Microsoft.Data.Sqlite's connection-string doc says: "Mixing shared-cache mode and write-ahead logging is discouraged. For optimal performance, remove `Cache=Shared` when the database is configured to use write-ahead logging." SQLite goes further: shared cache "is an obsolete feature… The use of shared-cache mode is discouraged. Most use cases for shared-cache are better served by WAL mode." Under shared cache the failure mode is `SQLITE_LOCKED` (table lock), which **`busy_timeout` does not retry** — busy handling is for `SQLITE_BUSY`, not `SQLITE_LOCKED`. So the "we added busy_timeout and it still fails" bug is guaranteed if `Cache=Shared` survives.
2. **Read-only + WAL needs write access to the directory.** SQLite: a read-only WAL database can be opened only if the `-shm` and `-wal` files already exist, or those files can be created, or the DB is opened `immutable`. `Mode=ReadOnly` plus a freshly checkpointed-and-deleted WAL, with a directory that denies creation, yields `SQLITE_CANTOPEN`/`SQLITE_READONLY_CANTINIT` — MCP prints "workspace database not found"-class errors while the file is plainly there.
3. **`busy_timeout` is not the connection string's `Default Timeout`.** `Default Timeout` (default 30 s) is the *command* timeout; there is no `busy_timeout` keyword. It must be set per connection (e.g. `PRAGMA busy_timeout=5000` in an `IDbConnectionInterceptor`/`DbConnection.StateChange` hook) because PRAGMAs other than `journal_mode` are per-connection, and `journal_mode` is persistent in the file header.
4. **Backups taken by copying `workspace.db` lose committed rows.** With WAL, committed transactions live in `-wal` until a checkpoint. `VACUUM INTO 'backup.db'` (or the backup API) is the only correct copy, and it is also the right pre-migration step.

**Why it happens:**
WAL is marketed as "more concurrency", so it looks like a one-line pragma. The second process (MCP) was written carefully to be read-only *by construction* — and that discipline is exactly what collides with the WAL `-shm` contract.

**How to avoid:**
- Remove `Cache=Shared` from **both** connection strings in the same commit that enables WAL. Leave `Mode=ReadOnly` on MCP, add `Pooling=True` (default) and a short `Default Timeout`, and set `PRAGMA busy_timeout` explicitly.
- Set the pragmas in one place: a `WorkspaceConnectionFactory`/interceptor that applies `journal_mode=WAL` (write path only), `busy_timeout=5000`, `foreign_keys=ON`, `synchronous=NORMAL` on every open. Test it by asserting `PRAGMA journal_mode` on a *pooled* connection, not just the first.
- Decide the MCP first-open story explicitly: either (a) MCP opens `Mode=ReadOnly` and documents "start the desktop once after install", or (b) MCP opens the DB `immutable=1` when `-wal` is absent (no writer), (c) or the installer pre-creates the file. Whatever you choose, the acceptance test is *"MCP starts against a workspace that the desktop has never opened since boot, in a read-only directory"*.
- Make backup a first-class command (`VACUUM INTO` + `PRAGMA quick_check` + restore drill), used by the migration path and exposed in diagnostics (N18/N49).
- Add the concurrency test that does not exist today: one writer loop (autosave) + one background scan writer + one MCP reader, 60 s, zero `SQLITE_BUSY`/`SQLITE_LOCKED`, and a bounded WAL size (a long-lived reader prevents checkpoint progress and the WAL grows until the reader ends — SQLite documents this explicitly).

**Warning signs:**
`SQLITE_BUSY`/`SQLITE_LOCKED` in the desktop log file (`%TEMP%\ads_desktop_crash.log` is already unbounded and shared by writers); `-wal` file growing past a few MB during a scan; MCP works while the desktop is open and fails after a reboot; a "backup" file that is missing the last few saves; `PRAGMA journal_mode` still returning `delete` after the change (pragma ran on a different pooled connection).

**Phase to address:**
**P4** (4.6, together with migrations) and **P5** (the concurrency/backup tests).

---

### Pitfall 6: gRPC limits and deadlines are tuned on the server, but the failure happens on the client

**What goes wrong:**
Four separate mechanisms, all with client-side defaults the server cannot change:

1. **Message size is capped on both ends.** `MaxReceiveMessageSize` defaults to **4 MB** for the server *and* for each client channel. The WASM and desktop clients register channels with `AddGrpcClient` and no `ConfigureChannel` (`src/AzureDesignStudio/Program.cs:59-84`, `src/AzureDesignStudio.Desktop/MauiProgram.cs:60-70`). So a server-side `MaxReceiveMessageSize = 16 MB` (N14) raises the upload ceiling while the *download* of a design (`DesignService.Load` returns the full payload) still dies at 4 MB with `ResourceExhausted` on the client. Any payload contract (N26) must set the cap on both sides, from one shared constant.
2. **A global deadline kills the deploy stream.** `DeployService.CreateDeployment` is server-streaming and polled every 2 s for the full ARM LRO (`DeployService.cs:1497-1540`); `DeployGrpcService.cs:191-195` consumes it with `MoveNext(default)` and no deadline. Microsoft: "There is no default deadline value. gRPC calls aren't time limited unless a deadline is specified." Adding a blanket `CallOptions.Deadline`/per-service deadline (the natural reading of "server deadlines" in N14) converts every long deployment into `DeadlineExceeded` — and per the docs, the server keeps running the method after the client aborts, so you get a *deployment that happened* and a UI that says it failed. Deadlines must be per-method: short for unary reads/writes, absent for streaming calls, with idle/heartbeat limits instead.
3. **Browser server-streaming needs `GrpcWebText`.** The gRPC-Web doc: `GrpcWebMode.GrpcWeb` sends unencoded content; "`GrpcWebMode.GrpcWebText` configures base64-encoded content. **Required for server streaming calls in browsers.**" All three WASM clients use `GrpcWebMode.GrpcWeb` (`Program.cs:59, 66, 79`), including `DeployClientWithAuth` whose `CreateDeployment` is server-streaming. That is a latent "deploy from the browser hangs until the stream ends / delivers nothing" bug, independent of any of the new limits.
4. **A rate limiter turns a gRPC call into an opaque HTTP error.** The middleware rejects with `503` by default and the docs' own workaround is an `OnRejected` callback that writes `429` + `Retry-After`. Neither carries a `grpc-status` trailer, so the .NET client surfaces `RpcException` with `StatusCode.Internal`/`Unavailable` and a message like "Bad gRPC response. HTTP status code: 429" — not `ResourceExhausted`. Every `catch (RpcException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)` in the shared UI silently stops matching, and the user sees a generic error. Also: `UseRateLimiter` must run after `UseRouting` when endpoint-scoped policies are used, and after `UseAuthentication` if the partition key is the user.
5. **Health endpoints and the authorization fallback collide.** N14's `/health/live` + `/health/ready` and N10's `FallbackPolicy` land in the same phase. With a fallback policy, `MapHealthChecks` requires auth unless `.AllowAnonymous()`, so Kubernetes probes get 401 and the pod restart-loops; and `app.UseHttpsRedirection()` sits before routing, so a probe on the HTTP port gets a 307 and fails. (The same redirect is in front of the desktop's cleartext HTTP/2 listener on `localhost:5204`; `Properties/launchSettings.json` configures an HTTPS URL, so this is worth a one-line verification — a 307 on a gRPC POST shows up as a confusing `Unavailable`.) **[INFERENCE]** for the 5204 redirect specifically; the redirect-vs-probe ordering follows from middleware order and is verified by the 307 behavior.

**Why it happens:**
"Add limits" reads like a server-side configuration task. Each of these limits has a paired client or proxy half that lives in a different project, and none of the halves are exercised by the current test suite (there are no server tests and no E2E at all).

**How to avoid:**
- Define the payload cap once: `AdsPayloadLimits.MaxDesignBytes = 16 MB` in a shared project, consumed by `AddGrpc(o => o.MaxReceiveMessageSize = ...)`, by the client `ConfigureChannel(o => o.MaxReceiveMessageSize = ...)`, and by the N26 validator. Test both directions with a payload one byte over.
- Never set a service-wide deadline. Add deadlines per call for unary RPCs; for `CreateDeployment` add a keep-alive frame every N seconds and a server-side *idle* timeout, and pass `context.CancellationToken` into the ARM polling loop so a user cancel actually stops work.
- Set `GrpcWebMode.GrpcWebText` for the Deploy client (server streaming) and keep `GrpcWeb` for unary clients; add a browser E2E that opens a deploy stream and asserts ≥ 2 frames arrive (P5).
- Give the rate limiter a gRPC-shaped rejection: `OnRejected` writing `grpc-status: 8` (ResourceExhausted) + `grpc-message`, or limit only the mutating services and keep unary reads generous. Partition by `oid` (not `IP`, which is the ingress IP behind nginx), exclude `/health/*`, and assert the client maps the rejection to a typed code.
- Order the pipeline deliberately: `UseRouting` → `UseAuthentication` → `UseAuthorization` → `UseRateLimiter` → `UseGrpcWeb` → endpoints; map health checks with `.AllowAnonymous()` and short-circuit them before `UseHttpsRedirection` (or exclude `/health` from redirect).

**Warning signs:**
`ResourceExhausted` on designs that save fine; a deploy that fails at a suspiciously round duration (30 s / 60 s / 100 s); `HttpClient.Timeout`/`DeadlineExceeded` in logs (the factory-created `HttpClient` has the platform default 100 s and the repo overrides it only for `ArmTransport`/`ResourceGraphClient` in `MauiProgram.cs:88-93`, not for gRPC); "Bad gRPC response. HTTP status code: 429"; health endpoints return 401/307 when curled; browser deploy spinner never updates but the deployment exists in Azure.

**Phase to address:**
**P4** (4.3) for limits/health, with the client-cap constant shared in the same change; **P5** for the streaming and rejection tests.

---

### Pitfall 7: Normalizing identity silently orphans every saved design

**What goes wrong:**
`ServiceTools.GetUserId` reads exactly one claim — `http://schemas.microsoft.com/identity/claims/objectidentifier` — and callers do `new Guid(userIdClaim)` (26 sites in `Server`, per the assessment). `DesignModel.UserId`, `AzureSubscriptionModel.UserId`, and the 10-design quota (`DesignService.cs:47-53`) are all keyed on that `Guid`. If the identity helper (N11) starts returning `sub`, `preferred_username`, or a hashed value, **nothing throws** — every existing user simply has zero designs, and new saves create a second, parallel namespace. If instead someone flips `JwtBearerOptions.MapInboundClaims` to `false` (a very tempting "modernization" while touching `Program.cs:24-29`), the legacy URI lookup returns `null` and every authenticated call degrades to 401 — the failure mode `CONCERNS.md` already documents.

Two adjacent traps land in the same phase:
- **App roles are not retroactive.** `roles` claims appear only when the user (or a group) is assigned the role on the *API's* app registration, and only in tokens issued afterwards; an hour-old cached access token has none. Microsoft also documents a sharp edge: "if you add a service principal to a group, and then assign an app role to that group, Microsoft Entra ID doesn't add the `roles` claim to tokens it issues." So a group-based `Deploy.Approver` design can silently never grant the role.
- **Scope changes are sticky in the browser.** `wwwroot/appsettings.json` holds `AzureAd:ClientId`/`ServerScope`, and `service-worker.published.js` caches every `.json` asset, so a scope or client-id change does not reach existing users until the service-worker version changes — a new `RequiredScope` on the server then 403s everyone who has not hard-refreshed.

**Why it happens:**
Claim extraction looks like a pure refactor with no persisted state. The state is in `UserId` columns and in MSAL token caches.

**How to avoid:**
- Make `oid` the canonical, immutable key. Accept the URI form and the short `oid` form, `Guid.TryParse` both, and return a typed 401/403 instead of throwing. Do **not** accept `sub` as a substitute for `oid`; if you need it, store it as a separate nullable column with its own index.
- Pin the behavior with tests that assert the extracted `Guid` for: (a) a token whose claims use the URI key, (b) one with `oid`, (c) one with neither. Use a frozen claim dictionary — this is a pure function and belongs in `Core.Tests`-style tests, not a live-Azure test.
- Land N11 **before** any JwtBearer option change, and add an explicit comment at `Program.cs:24-29` that `MapInboundClaims` is load-bearing for legacy claim shapes.
- For roles: require assignment on the API registration, prefer direct user assignment over group assignment (documented gap), and make the 403 message name the exact missing role. Add a "claims" diagnostic (`/diagnostics/whoami`, guarded) so the first admin can see what the token actually contains.
- Exclude `appsettings*.json` from `offlineAssetsInclude` and fetch it network-first (this is also a P2-sized fix that unblocks every future identity change).

**Warning signs:**
A user reports "all my designs are gone" right after a deploy; `SELECT COUNT(*) FROM AdsDesigns WHERE UserId = @me` returns 0 for a known user; the quota stops blocking; 401s with no exception in logs; a freshly role-assigned admin still gets 403 until they sign out and back in; two `UserId`s per person in the audit table.

**Phase to address:**
**P4** (4.1 identity, 4.5 guardrail/role scoping) — and the `appsettings.json` service-worker exclusion in **P2** (docs/lifetime bucket) so it is not blocked behind the gate.

---

### Pitfall 8: New test projects get added but never run — or run in a configuration that cannot boot

**What goes wrong:**
`build.yml` builds an **explicit project set** and then runs `dotnet test … --no-build`. A new `AzureDesignStudio.Server.Tests` project that is not added to the `Build` step will not exist at test time; with `--no-build` the test command fails (or, worse, silently runs the old binaries if the path is globbed). The same file also never builds `AzureDesignStudio.Desktop`, so component/E2E work has no compile gate.

The subtler trap is configuration. CI uses `-c Release`, which compiles the server **without the `DEBUG` symbol**. `Program.cs` then takes the non-debug branch: it *requires* `KeyVaultName` (throws `InvalidOperationException` when blank) and calls `builder.Configuration.AddAzureKeyVault(...)`. So a `WebApplicationFactory<Program>` test that just works locally (`Development`, `DEBUG`) fails in CI at host construction — before any test body runs — and the natural "fix" (setting a fake `KeyVaultName`) then attempts a real network call to a nonexistent vault. That is how a suite becomes "flaky" without a single flaky test.

The same phase introduces bUnit, which has its own configuration surface: components calling `IJSRuntime` throw under strict JSInterop mode unless the call is set up (loose mode + `SetupModule`/`Setup` per call), and components using AntDesign need `AddAntDesign()` in the test provider or they fail on missing services rather than on assertions.

**Why it happens:**
The test project template assumes the solution is the unit of build. This repo deliberately does not build the solution (Desktop is Windows-only), so every new project is a manual addition to two lists. And `#if DEBUG` in `Program.cs` selects *infrastructure*, not diagnostics — the exact debt `CONCERNS.md` lists as "`#if DEBUG` branches that change infrastructure".

**How to avoid:**
- Add the new project to the `Build` step **and** to the test step in the same PR, and change `--no-build` for that project or build it in the same job. A test project is not "added" until CI output shows its test count; N5's coverage collection is the mechanical proof.
- Refactor provider selection to configuration **before** the first integration test: `Database:Provider` = `InMemory | Sqlite | SqlServer`, `Crypto:Provider` = `Dev | KeyVault`, with `IsDevelopment()` gating only reflection and diagnostics. Then the test factory sets the config instead of fighting `#if`.
- Make `WebApplicationFactory` explicit about the pieces it replaces: test authentication handler that emits a known `oid`, SQLite in a temp file (not `InMemory` — you want the real relational constraints that `CONCERNS.md` flags as divergent), `NullCryptoService`, no Application Insights.
- For bUnit, standardize one base fixture: loose JSInterop by default, `AddAntDesign()`, and a `TestContext` wrapper that fails the test on an unexpected JS call *after* the assertion phase. Assert rendered text/attributes, not AntDesign internals.
- Isolate state: every SQLite test gets its own temp directory and `SqliteConnection.ClearAllPools()` in `Dispose` (the existing `WorkspaceTests` pattern) — pooled connections otherwise keep file locks and make Windows-only tests fail intermittently.

**Warning signs:**
CI green with fewer tests than locally; a test project that builds but has 0 discovered tests; `NETSDK1147`-class workload errors in the Windows job; `InvalidOperationException: KeyVaultName is not configured` inside `CreateHost`; tests that pass with `dotnet test` and fail with `-c Release`; `IOException` deleting temp DBs on Windows; a bUnit test failing with "bUnit's JSInterop has not been configured to handle the call".

**Phase to address:**
**P5** (N3/N2/N4/N5/N6) — but the `Database:Provider`/`Crypto:Provider` refactor is a **P4** prerequisite (it is also 4.2's local crypto provider), and the CI project-list discipline is **P3**.

---

### Pitfall 9: E2E becomes the slowest, flakiest required check — so it gets deleted

**What goes wrong:**
There is no E2E harness in the repo today, and the milestone wants two (WASM Playwright + WebView2 CDP). Everything that makes browser tests flaky applies here, plus two stack-specific ones: the WASM suite needs the app served with a seeded config and auth bypass, and the desktop suite needs an interactive Windows session with WebView2 — which behaves differently on a CI runner (no GPU, first-run WebView2 runtime install, `%LOCALAPPDATA%` state from a prior run). Playwright's own CI guidance is to set `workers: 1` on CI and always set a `globalTimeout`, precisely because an unbounded run gets killed by the job limit and produces no report.

**Why it happens:**
E2E is written against a warm, logged-in developer machine. CI starts cold, has no cached MSAL state, and re-uses the workspace file across runs unless it is isolated.

**How to avoid:**
- One config file per host, with `workers: process.env.CI ? 1 : undefined`, `retries: process.env.CI ? 2 : 0`, `globalTimeout`, and `reporter: [['junit', …], ['html', { open: 'never' }]]`; upload `playwright-report/` and traces on failure (the docs' GitHub Actions sample does exactly this).
- Do **not** cache Playwright browser binaries; the docs state restore time is comparable to download and OS deps are not cacheable.
- Seed determinism: a test-only `appsettings.E2E.json` with a fixed client id/scope and an auth stub, a fresh `workspace.db` per run under `%LOCALAPPDATA%`, and `ASPNETCORE_ENVIRONMENT=E2E`. Never reuse the developer's workspace file.
- Give each test a unique temp app-data root; assert zero console errors and zero unhandled exceptions as part of the smoke (the plan's N2 already lists this).
- Treat the desktop CDP suite as **nightly or Windows-PR-only** from day one, and keep the WASM suite on every PR — mixing them makes the PR gate hostage to the slower, less stable one.
- Quarantine by name with an expiry date (N5), and make a quarantined test *fail* when it expires rather than vanish.

**Warning signs:**
Retry count climbing while the pass rate on first attempt drops; screenshots that differ only in focus ring/title bar; tests passing locally and failing only on the runner; a suite that hangs and produces no report; the job time growing with the number of tests rather than staying proportional to app boot.

**Phase to address:**
**P5** (N2), hardened in **P6** when the Windows job becomes blocking.

---

### Pitfall 10: AutoMapper removal is a generator change, and the one hard case is runtime-type dispatch

**What goes wrong:**
The migration looks like a find-and-replace over 118 `IMapper` references. It is not, for two reasons:

1. **`AzureNodeProfile` is generated.** `src/AzureDesignStudio.SourceGeneration/Templates/AzureNodeProfile.scriban` renders the profile from `[MapToDto]` models, so "remove AutoMapper" means editing the Scriban template and the generator's contract test (`DtoGeneratorContractTests`), not just the call sites. The DTO goldens (`StencilExportGoldenTests`, `NewType…` tables) are the parity harness — use them as the acceptance gate, one mapping family per PR.
2. **One caller maps by *runtime* type.** `DataModelFactory.GetNodesDto` (`DataModelFactory.cs:261-273`) currently sidesteps AutoMapper entirely with `JsonSerializer.Deserialize(JsonSerializer.Serialize(d), derivedType)` — a string allocation plus a second object **per node**, and the reflection serializer overload with a computed `Type`. A source generator (Mapperly) cannot switch on `Type` at compile time, so this node needs a generated `switch` dispatcher (`GetDtoTypeFromKey` already exists) or a hand-written `case` table. If you delete the JSON round-trip without providing that dispatcher, design load breaks for every derived node type — which is every node.

The trimming half is worse than the mapping half: `PublishTrimmed=true` and `RunAOTCompilation=true` (Release) are on, and CI **never publishes trimmed** — it runs `dotnet build` only (`.github/workflows/build.yml:26-35`). `CONCERNS.md` records that this exact class of breakage shipped once before, and the csproj carries three workarounds (a `TrimmerRootAssembly` for MSAL and `System.Collections.Immutable`, a pinned `System.Formats.Nrbf`, and the `DropForeignThreadingAnalyzers` target). Removing a reflection mapper improves the odds but does not prove the payload loads; only a trimmed publish + boot does. Failure is runtime-only: a green CI and a published app that maps nothing.

**Why it happens:**
AutoMapper removal is scoped as "N46, M-sized" in v3, and the generator + AOT surface is invisible from the call sites.

**How to avoid:**
- Sequence: edit the Scriban template → regenerate → run DTO/golden parity tests → remove the `AddAutoMapper` registration and the two package references → delete the `NoWarn=NU1903` suppression (the vulnerable-package gate's dated allowlist entry should expire in the same PR).
- Build the type dispatcher explicitly and test it for base DTOs, derived DTOs, `JsonElement` nodes, and an unknown `TypeKey` (must degrade to `UnknownNodeModel`, per `StencilExportGoldenTests`).
- Add a **real** trimmed-publish job (WASM `dotnet publish -c Release` + a headless boot/smoke) before claiming the AOT budget is measured. Without it, "AOT verified" is an assertion, not a check.
- Keep the mapping change and the trimming claim in separate commits so a bisect can distinguish "mapping wrong" from "trimmed away".
- Do the same for `ResourceBaseJsonConverter` (`Activator.CreateInstance`) — that is the next reflection site and is on the same serialize path.

**Warning signs:**
Goldens updated in the same PR as the mapper (you are moving the goalposts); a design loads with unnamed/untyped nodes; the published app shows an empty canvas while `dotnet run` works; `MissingMethodException`/`JsonException` only in a published build; the `NoWarn` survives the dependency removal.

**Phase to address:**
**P6** (N46, preceded by N47), with the trimmed-publish job added in **P3/P5** so there is a signal before the change lands.

---

### Pitfall 11: Theme and accessibility work fights AntDesign 1.6.2's actual packaging

**What goes wrong:**
The locked decision in `docs/top-100-ui-improvements.md` is "variable + dark CSS layers mapped to `--ads-*`, never a fork of component internals". The shipped implementation does something different: `window.AdsTheme.apply` (`wwwroot/js/ads.js:242-255`) swaps a **single** `<link id="antd-theme">` between `_content/AntDesign/css/ant-design-blazor.css` and `…ant-design-blazor.dark.css`. Verifying the package (AntDesign 1.6.2 in the NuGet cache) shows why both approaches "work" and why they must not be mixed: `ant-design-blazor.css`, `.dark.css`, `.compact.css`, and `.variable.css` are each **complete, standalone stylesheets of ~690 KB** — `.dark.css` is not an overlay that expects `.variable.css` underneath. Load both and you get duplicate resets with specificity-dependent winners (AntDesign's own CSS is component-scoped but not layer-ordered); load neither and components are unstyled.

Consequences to design around:
- The swap downloads a second ~690 KB stylesheet on first toggle (the published service worker precaches code/dll/json assets — `offlineAssetsInclude` in `wwwroot/service-worker.published.js` — but not `_content/**/*.css`), so dark mode's first paint is slow and the AntDesign `<link>` is already pointing at light CSS during the inline boot script (which only sets `data-ads-theme`). That is a visible flash.
- The `data-ads-theme` attribute is set in an inline script before first paint (good), but the AntDesign href is not — so the two theming systems are applied at different times and a screenshot at t=0 differs from t=100 ms.
- Accessibility is not solved by tokens: the plan's own baseline lists `div`/`title` handlers for nodes and the sider toggle with no keyboard semantics. AntDesign 1.6.2 has no `ConfigProvider.Theme`, so you cannot fix focus/color inside a theme object — it is CSS + component markup work. (The `maximum-scale=1.0, user-scalable=no` blocker from the 2026-10-20 audit is **already gone** in both hosts: `wwwroot/index.html:6` and `Desktop/wwwroot/app.html:6` now read `initial-scale=1.0` — cite the responsive-layout work, not zoom locking, when planning U-items.)
- A hardcoded-style gate is only useful if it is scoped: the current counts (2026-10-21) are **58 hex literals** under `src/AzureDesignStudio/wwwroot/css` and **67 `style="` occurrences** across Razor in the WASM and Core projects, plus vendored `prism.css` and `_content/**` which must be excluded or the gate is unusable.

**Why it happens:**
AntDesign's Blazor port has a different theming model from Ant Design React (no runtime token provider), so React-era advice is misleading; and CSS is the one part of the app with no test, so every claim about it is unverified.

**How to avoid:**
- Pick **one** axis and write it down in `AGENTS.md`: (A) complete-theme swap (current), documented as such, with `preload` for the inactive theme and a service-worker entry for both; or (B) `variable.css` base + `.dark.css` **only** if you verify in the browser that it is an override layer. Do not ship a hybrid.
- Set the AntDesign href in the same inline boot script that sets `data-ads-theme` (one attribute write + one href write before paint), and add a Playwright assertion that computed background of a known element is dark on first paint in dark mode.
- Gate the token work mechanically: a script that fails on new hex/`style=` outside an allowlist of vendored files, run in CI (U1's "hardcode gate"), and treat the existing 58/67 as a baseline that may only go down.
- Do accessibility as markup changes with an axe gate in the existing Playwright suite (U96 + assessment proposal 4): keyboard-only palette walkthrough, focus-visible assertions, and no `title`-only affordances. Add the axe rule set to the same report so a regression names the rule.

**Warning signs:**
Dark mode with light AntDesign tables/modals; a flash on toggle or on load; two stylesheets in the DOM; a11y "fixes" that only change colors; the hardcode gate disabled with a comment instead of an allowlist; axe violations that appear only in the desktop WebView2 host.

**Phase to address:**
**P2** (theme decision recorded; the token/hardcode gate and axe gate are listed as allowed pre-gate Round 2 items) and **P6** (Round 2 surfaces open only after G6). The a11y gate itself belongs to **P5**'s E2E work.

---

### Pitfall 12: The guardrail/role gate is satisfied in tests but not in the running pipeline

**What goes wrong:**
N10 adds tenant scoping and an admin role to `SetGuardrailPolicy`, and N11 changes how the user is read. Both live on the same authorization path, and there is no `FallbackPolicy` today — `MapGrpcService` is called without `RequireAuthorization()` (`Program.cs:169-183`), so security depends on remembering `[Authorize]`/`[RequiredScope]` per class. Adding an admin policy to one RPC while leaving the fallback open means a new RPC (N21 workspace, N41 MCP-adjacent tools) is anonymous by default. Conversely, adding a `FallbackPolicy` in the same change flips `/health` and `RetailPriceService` (intentionally anonymous, documented at `RetailPriceService.cs:10-13`) to 401 — the classic "hardening broke the health probe and the price feed" release.

**Why it happens:**
Three authorization changes land in one phase, each with a different default, and the defaults are what actually protect the surface.

**How to avoid:**
- Land the fallback in a dedicated commit with the exceptions enumerated: `FallbackPolicy = RequireAuthenticatedUser()`, `.AllowAnonymous()` on `RetailPriceService` and every `MapHealthChecks` endpoint, and a test that hits *every* mapped service method without a token and asserts 401 (this is the only way to prove the default).
- Express the role policy once (`AddPolicy("Deploy.Approver", p => p.RequireRole(...))`) and apply it to `ApprovePlan`, `ExecuteDecomWindow`, `SetGuardrailPolicy`, and any new mutating RPC; add a self-approval check as a second, independent condition so a role misconfiguration does not silently restore self-approval.
- Migrate the existing `GuardrailRowId = 1` row to the default tenant in a migration, and make the read path tenant-scoped — a policy row that no tenant owns must fail closed.
- Test the *negative* cases as first-class: non-admin 403, cross-tenant 404 (not 403, to avoid existence leaks), anonymous 401, and health 200 anonymous.

**Warning signs:**
A new service is reachable with `curl` and no token; a role check that only exists as an attribute on one method; `/health` 401 in a probe log; `RetailPriceService` returning 401 after a hardening release; approval and apply both performed by the same `ApprovedByUserId` in the audit table.

**Phase to address:**
**P4** (4.5) with the test matrix landing in **P5**.

---

## Technical Debt Patterns

| Shortcut | Immediate Benefit | Long-term Cost | When Acceptable |
|---|---|---|---|
| Keep `#if DEBUG` selecting database/crypto provider | Zero config for developers | Release-only tests cannot boot; the provider you test is not the provider you ship (already true for InMemory vs SqlServer) | Never once a server test project exists — replace with `Database:Provider` config in P4 |
| Leave `EnsureCreated()` in `DesignService`/`DeployService` constructors | First call self-heals a missing schema | Runs per RPC; wins the race against `Migrate()` and creates schema without history | Never; delete in the same PR as the baseline migration |
| Sign the MSIX with `signtool` as the last YAML step | No MSBuild certificate plumbing in the repo | Certificate subject and manifest publisher drift; the "unsigned if secret missing" branch can ship a release | Acceptable only while the packaging job is non-blocking; require it in P6 |
| `Cache=Shared` on the workspace and MCP connections | Slightly fewer file handles | Incompatible with WAL; converts `SQLITE_BUSY` (retryable) into `SQLITE_LOCKED` (not retryable) | Never after WAL (both docs say remove it) |
| JSON serialize/deserialize to re-type nodes | One line, works for every derived DTO | Per-node GC churn on load; reflection overload; blocks trimming | Acceptable only until the type dispatcher exists (P6) |
| Swap whole 690 KB AntDesign theme files at runtime | Two lines of JS, zero build integration | Second download on toggle, FOUC, and a documented strategy (variable+dark) that the code contradicts | Acceptable for one release if preloaded and documented; not as the final design |
| Non-blocking Desktop CI job | Unblocks merges while timing is unknown | Becomes permanent; the "builds on PRs" requirement is unmet | Acceptable exactly as long as P3 says: two consecutive green runs, then make it blocking |
| `NoWarn=NU1903` for AutoMapper | Unblocks restore today | Hides a High-severity advisory and the trimming hazard behind it | Until N46 lands; the vulnerable-package gate should carry a dated allowlist entry so it expires |

## Integration Gotchas

| Integration | Common Mistake | Correct Approach |
|---|---|---|
| **MSIX / SignTool** | Assume a valid signature is enough | The cert must be in `LocalMachine\TrustedPeople` on the target machine (not `CurrentUser`); the manifest `Publisher` must equal the cert subject string exactly |
| **MAUI workload on GitHub Actions** | `dotnet workload install maui` (all platforms) on every job | `dotnet workload restore <Desktop.csproj>` scoped to the project, in its own cached job; the runner image does not pre-install MAUI |
| **Entra ID app roles** | Expect `roles` immediately after assignment | New token required; group→role assignment does not emit `roles` for a service principal in the group; prefer direct assignment and document "sign out and back in" |
| **Entra ID scopes** | Rename `Server.Access` on the server | Old tokens carry the old `scp`; every client 403s until re-auth — and the WASM service worker caches `appsettings.json`, so pin the change behind a service-worker version bump |
| **gRPC-Web in Blazor** | Use `GrpcWebMode.GrpcWeb` for every client | Server-streaming in browsers needs `GrpcWebText`; client/bidi streaming is unsupported entirely — do not add such RPCs |
| **Rate limiter + gRPC** | Rely on a 429 status being visible as `ResourceExhausted` | Write `grpc-status`/`grpc-message` in `OnRejected`, or limit only mutating methods; partition by `oid`, not IP (nginx ingress) |
| **SQLite backup** | `File.Copy(workspace.db)` | Use `VACUUM INTO` (or the backup API) and `PRAGMA quick_check`; a WAL copy without `-wal` silently loses commits |
| **MCP stdio** | Log to stdout, or open with `WorkspaceDb` | Keep all diagnostics on stderr (already correct) and never use the bootstrap class in MCP: it writes schema |
| **Key Vault in CI** | Set a placeholder `KeyVaultName` to get past the startup check | The placeholder makes `AddAzureKeyVault` attempt a real endpoint; select a dev crypto/config provider by configuration instead |

## Performance Traps

| Trap | Symptoms | Prevention | When It Breaks |
|---|---|---|---|
| Desktop CI job installs MAUI workloads cold | Job 15–25 min; skipped checks | Project-scoped `workload restore` in a cached job; split build and packaging | First PR after any SDK band bump |
| Server-streaming deploy with fixed 2 s polling | 600 frames over one HTTP/2 stream; ingress timeouts | Backoff after the first minute; keep-alive frames; per-method (not global) deadlines; documented `proxy-read-timeout` | Deployments longer than the shortest proxy timeout (~60 s) |
| Long-lived reader blocks WAL checkpoint | `-wal` grows without bound during a scan | Bound MCP read transactions; checkpoint after scans; monitor file size | A scan of ~5k resources with a concurrent long query |
| JSON round-trip per node on design load | Load time and allocations scale with node count | Generated type dispatcher (P6) | Designs with hundreds of nodes, on WASM/AOT |
| Workspace growth with no retention | Multi-GB `workspace.db`; slow `VACUUM`; long backup | N19 retention + incremental vacuum; size shown in diagnostics | ~1 year of weekly scans of a 5k-resource tenant |
| Full-theme CSS swap | Perceived flash; duplicated bytes | Preload the inactive theme; precache it; or variable-based theming | Every first dark-mode load |
| E2E suite that boots the app per test | Run time grows multiplicatively | One server/app instance per worker; `webServer` reuse; 1 worker on CI | Suite > ~20 tests |

## Security Mistakes

| Mistake | Risk | Prevention |
|---|---|---|
| Shipping a release with the unsigned branch | Users install with "Publisher: unknown"; the gate is unverifiable | Fail the release job when `MSIX_PFX` is absent (only allow unsigned on `workflow_dispatch` with an explicit input) |
| PFX password passed on the `signtool` command line | Leaks into runner process args/logs | Use `signtool sign /f … /p` only with a secret-scanned runner; prefer `AzureSignTool` with certificate-store access, or Azure Artifact Signing |
| Dev crypto provider registered in production | Subscription secrets encrypted with a DPAPI key on a server disk | Select by configuration and refuse `Dev` when `!IsDevelopment()`; assert in a startup test |
| `FallbackPolicy` added without removing the fail-open assumption | New RPCs anonymous by default today; or `/health` and price feed break tomorrow | Explicit allow-anonymous list + a test that hits every method unauthenticated |
| Guardrail policy still a single global row | Any authenticated caller can widen the protect-nothing policy | Tenant + role scoping (N10), self-approval check, migration of row 1 |
| Secrets in `workspace.db` unencrypted | Diagnostic bundle or backup exposes them | Redact in N49's bundle; separate the subscription store from the design workspace if N9 slips |
| CSP added without auditing inline scripts | Either a nonce/hash regime late in the milestone, or CSP in report-only forever | Inventory inline scripts (`index.html` App Insights snippet, theme boot script, `ads.js`) early in P6; prefer external files with hashes |
| `allowedHosts: "*"` with forwarded headers | Host-header confusion behind ingress | Configure `AllowedHosts` when CSP/hardening lands |

## UX Pitfalls

| Pitfall | User Impact | Better Approach |
|---|---|---|
| Rate-limited gRPC surfaces as "an error occurred" | User retries into the same wall | Map the rejection to a typed message ("Server busy, retry in N s") using the `Retry-After` the limiter emitted |
| 403 for a missing role with no role name | Admin cannot self-diagnose | Name the role, log the claim set, offer a copyable request id (N49) |
| Deadline-less deploy stream that dies on a proxy timeout | "Deployment failed" for a deployment that succeeded | Keep the stream as a subscriber, not the driver (v3's scaling path); on reconnect, reconcile by `ApplyId` |
| Dark mode flash / partially themed components | Reads as broken | Decide one theming axis, set the href before paint, preload |
| Empty palette with no error when `ads-stencils.json` fails | App looks broken, nothing logged | Log + diagnostic banner at startup (already a known bug in `CONCERNS.md`) |
| Signed-but-untrusted MSIX in pilot | User cannot install and blames the product | Ship the cert + a one-line `Import-Certificate` instruction with the pilot build, and test on a clean VM |
| Health endpoint returning 401 | Operator cannot distinguish "unhealthy" from "misconfigured" | Anonymous `/health/*`, with readiness reporting migration state |

## "Looks Done But Isn't" Checklist

- [ ] **Signed MSIX:** often missing trust and identity — verify `Get-AuthenticodeSignature` is `Valid`, `Get-AppxPackageManifest` Publisher equals the cert subject, and install succeeds on a **clean** VM with no dev certificate.
- [ ] **Migrations:** often missing the baseline — verify `__EFMigrationsHistory` has exactly the baseline row on an upgraded file, row counts are unchanged, and `PRAGMA quick_check`/`DBCC CHECKCONSTRAINTS` is clean.
- [ ] **WAL:** often missing the second process — verify MCP reads while the desktop writes a scan, and reads a workspace the desktop has not opened since boot.
- [ ] **Limits:** often missing the client half — verify an oversized **download** (server → client) fails with the intended typed error, not the 4 MB default.
- [ ] **Deadlines:** often missing the streaming exemption — verify a > 5 minute `CreateDeployment` stream survives and emits frames.
- [ ] **Rate limiting:** often missing gRPC semantics — verify the client sees `ResourceExhausted` (or a typed app code), not "Bad gRPC response".
- [ ] **Health:** often missing anonymous access and redirect exclusion — verify `curl http://host/health/ready` returns 200 without a token and without a 307.
- [ ] **Identity:** often missing persisted data — verify a design saved by a v2 build still lists under the same user after the change; verify both claim shapes in a test.
- [ ] **Test projects:** often missing CI wiring — verify the new project's test count appears in CI output with `-c Release`.
- [ ] **E2E:** often missing failure artifacts — verify a deliberately failing test uploads a trace + screenshot and that the run terminates at `globalTimeout`.
- [ ] **AutoMapper removal:** often missing a trimmed publish — verify `dotnet publish -c Release` of the WASM app boots and loads a design with nodes of ≥ 3 derived types.
- [ ] **Theme:** often missing the second host — verify dark mode in both `wwwroot/index.html` and `Desktop/wwwroot/app.html`, at first paint, with AntDesign components visible.
- [ ] **Accessibility:** often missing keyboard paths — verify the palette, sider toggle, and node selection are usable with Tab/Enter/Escape and pass the axe rule set.
- [ ] **Guardrails:** often missing the negative path — verify a non-admin gets 403 and a second tenant cannot read or overwrite the policy.

## Recovery Strategies

| Pitfall | Recovery Cost | Recovery Steps |
|---|---|---|
| Half-migrated `workspace.db` | HIGH | Stop the app; restore the `VACUUM INTO` backup; ship a repaired migration that dedupes/validates first; add the upgrade fixture that would have caught it |
| Migration applied without baseline history | MEDIUM | Do not re-run; compute the applied set from `sqlite_master`/`sys.objects`, insert the baseline row, verify with a fresh `Migrate()` dry run against a copy |
| MCP cannot open a WAL file | LOW | Start the desktop once to materialize `-shm`, or open `immutable=1` when no writer is present; then fix the open mode |
| Rate limiter blocking legitimate deploys | LOW | Raise the permit or scope the policy to mutating methods; the limiter is in-process and takes effect on restart (note: two replicas keep separate counters) |
| Identity change orphaned designs | HIGH | Revert the claim-extraction commit (data is not lost, just unreachable), re-land with `oid`-only extraction and a fixture test; if a second namespace was created, write a one-off merge by display name **only** with human confirmation |
| Signed package rejected on a clean machine | LOW | Import the cert into `LocalMachine\TrustedPeople`; if Publisher mismatches, repackage with the corrected manifest value — the signature must be regenerated |
| CI gate made blocking while flaky | MEDIUM | Re-enable `continue-on-error` for one week, quarantine the named tests with expiries, fix the seed/state isolation, then re-tighten |
| AutoMapper removal broke node loading | MEDIUM | Revert the template change; keep the generated dispatcher and the `ToDto` work separately; re-land one mapping family per PR with goldens unchanged |
| Theme change shipped with a flash | LOW | Move the href swap into the inline boot script; add `preload`; if the axis choice is wrong, revert to the single-theme swap and update the doc rather than the code |

## Pitfall-to-Phase Mapping

| Pitfall | Prevention Phase | Verification |
|---|---|---|
| 1. MSIX identity/version/signing disagreement | P3 (packaging job), P6 (signing gate) | `Get-AppxPackageManifest` Name/Publisher asserted in CI; clean-VM install in the ship checklist |
| 2. MAUI workload cost → permanent non-blocking | P3 | Recorded job duration + two consecutive green runs before removing `continue-on-error` |
| 3. `EnsureCreated` → migrations without history | P4 (4.6) | Baseline row present; upgrade fixture row counts unchanged; no `EnsureCreated` call sites left |
| 4. Baseline migration fails on existing data | P4, P5 | Dedupe/diagnostic counts logged; upgrade fixture built from a real file |
| 5. WAL × read-only MCP × shared cache | P4 (4.6), P5 | Concurrent writer+reader test; MCP first-open test; `VACUUM INTO` restore drill |
| 6. gRPC limits/deadlines/streaming/rejection | P4 (4.3), P5 | Oversized download test; > 5 min stream test; rate-limit mapping test; anonymous health check |
| 7. Identity normalization orphaning data | P4 (4.1/4.5), P2 (SW cache) | Both claim shapes tested; existing design list unchanged; role 403 names the role |
| 8. Test projects not built, or booting in Release | P5 (P4 prerequisite refactor) | CI shows per-project test counts; `-c Release` integration tests green offline |
| 9. E2E slow/flaky | P5, P6 | `workers:1`, `globalTimeout`, traces uploaded, quarantine expiry enforced |
| 10. AutoMapper removal + trimming regression | P6 (N46), signal from P3/P5 | Goldens unchanged; trimmed publish boots and loads typed nodes |
| 11. AntDesign theming + a11y | P2 (decision + gates), P5 (axe), P6 (surfaces) | One theming axis documented; first-paint assertion; axe rule set in E2E; hardcode baseline not increasing |
| 12. Authorization defaults | P4 (4.5), P5 | Unauthenticated sweep of every mapped method; health/price-feed anonymous; cross-tenant 404 |

## Sources

**Primary (HIGH confidence)**
- MSIX troubleshooting guide — publisher/certificate mismatch `0x8007000B`, `TrustedPeople` machine store, `0x800B0109`: https://learn.microsoft.com/en-us/windows/msix/msix-troubleshooting-guide
- MSIX app package requirements — the 4th version section "must be left as 0": https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements
- Publish a .NET MAUI app for Windows (manifest is the MSIX authority): https://learn.microsoft.com/en-us/dotnet/maui/windows/deployment/overview?view=net-maui-10.0
- Publish packaged apps via CLI — `WindowsPackageType` must be explicit for Blazor Hybrid, `RuntimeIdentifierOverride`/WindowsAppSDK #3337, .NET 10 RID change: https://learn.microsoft.com/en-us/dotnet/maui/windows/deployment/publish-cli?view=net-maui-10.0
- EF Core — Create and Drop APIs ("EnsureCreated and Migrations don't work well together", transition is not seamless): https://learn.microsoft.com/en-us/ef/core/managing-schemas/ensure-created
- EF Core — Migrations overview / Applying migrations (history table, migration locking, SQLite has no idempotent scripts): https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/ , https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying
- EF Core SQLite limitations — table rebuilds only for model artifacts, `__EFMigrationsLock` abandoned-lock recovery, no idempotent scripts: https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations
- SQLite — Write-Ahead Logging (read-only WAL rules since 3.22, no network filesystems, checkpoint blocked by long readers): https://www.sqlite.org/wal.html
- SQLite — Shared-Cache Mode ("obsolete… discouraged"; `SQLITE_LOCKED` under table locks): https://sqlite.org/sharedcache.html
- Microsoft.Data.Sqlite — connection strings (`Mode`, `Cache`, `Foreign Keys`, `Default Timeout` is a command timeout; "Mixing shared-cache mode and write-ahead logging is discouraged"): https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings
- gRPC for .NET configuration — `MaxReceiveMessageSize` default 4 MB on server and client: https://learn.microsoft.com/en-us/aspnet/core/grpc/configuration?view=aspnetcore-10.0
- gRPC deadlines and cancellation — "no default deadline value": https://learn.microsoft.com/en-us/aspnet/core/grpc/deadlines-cancellation?view=aspnetcore-10.0
- Use gRPC in browser apps / gRPC-Web — `GrpcWebText` required for server streaming in browsers; client/bidi streaming unsupported: https://learn.microsoft.com/en-us/aspnet/core/grpc/grpcweb?view=aspnetcore-10.0
- Rate limiting middleware — `OnRejected`, `429`, ordering after `UseRouting`: https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0
- Add app roles and get them from a token — `roles` only in tokens issued after assignment; group→service-principal gap: https://learn.microsoft.com/en-us/entra/identity-platform/howto-add-app-roles-in-apps
- Playwright CI — `workers: 1`, `globalTimeout`, don't cache browsers, artifact upload: https://playwright.dev/docs/ci
- Mapperly — source-generated, "no reflection… trimming safe and AOT friendly": https://mapperly.riok.app/

**Secondary (MEDIUM confidence)**
- actions/runner-images #6936 — MAUI workloads not pre-installed; install time "8min+ on windows agents" (2023) / ~2 min (2025): https://github.com/actions/runner-images/issues/6936
- bUnit — emulating `IJSRuntime` (loose vs strict, `SetupModule`): https://bunit.dev/docs/test-doubles/emulating-ijsruntime.html
- ant-design-blazor #2641 — theme switching is a stylesheet swap / CSS variables: https://github.com/ant-design-blazor/ant-design-blazor/issues/2641

**Confidence method.** Tiers reflect what was actually read: *Primary* claims come from fetching the official page and reading the relevant section; *Secondary* come from issue threads, community answers, or search snippets. The `research-plan` seam's `classify-confidence` reports `websearch` → LOW and `context7` → MEDIUM, which is why web-sourced mechanics were promoted only after the primary page was read. Statements marked **[INFERENCE]** in place (middleware-order consequences, the `HttpClient.Timeout` effect on a > 100 s stream) follow from documented defaults plus the repo's configuration but were not measured on this machine. Nothing in this file was validated by running a build, a migration, or a signed install.

**Repo evidence (verified in this session, not externally sourced)**
- `src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj` — `WindowsPackageType=None`, `ApplicationId`, `ApplicationVersion=1`.
- `src/AzureDesignStudio.Desktop/Platforms/Windows/Package.appxmanifest` — `Identity Name="AzureDesignStudio.Desktop" Publisher="CN=Azure Design Studio" Version="1.0.0.0"`, `$placeholder$` assets.
- `.github/workflows/publish.yml` — workload install, `-p:WindowsPackageType=MSIX`, version regex stamping, conditional signing.
- `.github/workflows/build.yml` — explicit four-project build, `dotnet test --no-build`, Ubuntu-only.
- `src/AzureDesignStudio.Server/Program.cs` — `#if DEBUG` InMemory vs `#if !DEBUG` Key Vault + SqlServer, `EnsureCreated` + raw DDL, `UseHttpsRedirection`, no limits/health/rate limiter, fail-open mapping.
- `src/AzureDesignStudio.Server/Services/{DesignService,DeployService,ServiceTools}.cs` — constructor `EnsureCreated`, legacy URI claim, `new Guid(...)`.
- `src/AzureDesignStudio.Workspace/WorkspaceDbContext.cs` — `Cache=Shared`, `EnsureCreated`, `EnsureAddedColumns`, `EnsureAdditiveTables`, unique indexes declared in the model.
- `src/AzureDesignStudio.Mcp/AdsWorkspace.cs` — `Mode=ReadOnly` + `Cache=Shared`.
- `src/AzureDesignStudio/Program.cs` — `AddGrpcClient` with no channel options; `GrpcWebMode.GrpcWeb` for all three clients; `AddAutoMapper(typeof(AzureNodeProfile))`; `BuildServiceProvider()` inside handler factories.
- `src/AzureDesignStudio/wwwroot/js/ads.js:228-274` — single-link theme swap between complete AntDesign stylesheets.
- `src/AzureDesignStudio/AzureDesignStudio.csproj` — `PublishTrimmed`, Release-only `RunAOTCompilation`, `TrimmerRootAssembly` workarounds, `NoWarn=NU1903`.
- `src/AzureDesignStudio.Core/DataModelFactory.cs:261-273` — per-node JSON round-trip for runtime-type dispatch.
- `src/AzureDesignStudio.Server/Models/DesignDbContext.cs` — six audit tables in the model, `ADS_CHECK_JSON` constraint, `ApplyId` indexes.
- Counts taken 2026-10-21: 58 hex literals in `src/AzureDesignStudio/wwwroot/css`; 67 `style="` occurrences in Razor under the WASM and Core projects; AntDesign 1.6.2 ships four ~690 KB standalone stylesheets.

---
*Pitfalls research for: subsequent milestone — Trust Gate, Round 2 shell, signed MSIX*
*Researched: 2026-10-21*

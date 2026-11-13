---
type: audit-fix-and-improvement-plan
date: 2026-11-12
baseline: 93dc172
status: implemented (four passes, telemetry removal and the finishing pass; see sections 6-10)
supersedes: .planning/audits/2026-10-30/FIX-PLAN.md (FIX-01..04 are folded in here)
---

# Repository audit, fixes and improvement plan

An audit supplement, not a roadmap change: `STATE.md`, `ROADMAP.md` and the phase counters are
untouched. Everything below is in the working tree on top of `93dc172`; nothing is committed.

Method: four independent read-only sweeps (server, engines, core, client/desktop) plus a manual pass
over build/CI/Docker. Every finding was re-read in the code before it was changed, and claims about
Azure's wire formats were checked against the Microsoft Learn references cited inline.

## 1. What was wrong and is now fixed

### Build, CI, containers

| # | Defect | Fix |
|---|---|---|
| B1 | **Fresh restore fails the repo's own audit gate.** `Microsoft.AspNetCore.Components.WebAssembly.Server` 10.0.13 has a high-severity advisory (GHSA-g27p-qf6j-c96c, RCE, published 2026-10-13); with warnings-as-errors the Server no longer restores. | Bumped to 10.0.15 in `Directory.Packages.props`; Server lock file regenerated (only that package and its two transitive companions changed). |
| B2 | **`build.yml` was not a valid workflow**: `uses: github/codeql-action/autobuild` has no `@ref`, which GitHub rejects for the whole file - so no job in it could run. Autobuild would also have picked the solution, whose MAUI project cannot build on ubuntu. | CodeQL job builds Server + Mcp explicitly, `build-mode: manual`, action v4, SDK from `global.json`. |
| B3 | **Linux Release build needs a workload nobody installs.** The WASM client turns on AOT in Release; without `wasm-tools` that is `NETSDK1147`. | Linux build passes `-p:RunAOTCompilation=false` (the Docker step still does the full AOT build). Release `wasm` and `desktop-msix` jobs install the workload. |
| B4 | **The Docker image could not be built from either context.** From `src/` (CI) the central `Directory.Packages.props`/`global.json` are outside the context; from the repo root (`publish.yml`, README, AGENTS.md) every `COPY` path was wrong. `Discovery` was missing from the restore layer. | Dockerfile rewritten for a repo-root context; `.dockerignore` moved to the root; `DockerBuild.ps1` takes `-Tag`, fails on any step, and is used by `publish.yml` too. `DockerfileContext` set for Visual Studio. |
| B5 | Floating `sdk:10.0` image and `dotnet-version: 10.0.x` roll to the next feature band, which `global.json` (10.0.4xx) rejects. | Image pinned to `sdk:10.0.401`; every workflow uses `global-json-file`. |
| B6 | `codeql-analysis.yml` used retired `codeql-action@v2`, `checkout@v3` and the runner's default SDK. | v4 / v4 / `global.json`; build step limited to the C# leg. |
| B7 | Image listened on 8080 (.NET 8+ default) while the manifests and `EXPOSE` say 80. | `ASPNETCORE_HTTP_PORTS=80`. |
| B8 | `GenerateVersioningTargets.ps1` used an undefined `$gitCommitDateTicks`; its generated `src/Directory.Build.targets` shadowed the root one. | Ticks computed from the nbgv output; generated file chains to the root targets (smoke-tested: a build through it stamps the overridden version). Generated files are git-ignored. |
| B9 | Kubernetes manifest ran 2 replicas against an in-process what-if token gate (preview on pod A, approve on pod B -> `WHAT_IF_STALE`). | `replicas: 1` with the reason recorded. |

### Server

| # | Defect | Fix |
|---|---|---|
| S1 | **The server listened only on `127.0.0.1:5204`.** A code-defined Kestrel endpoint replaces every URL-configured one, so the browser endpoints and the container port were never bound (verified by running it: "Overriding address(es) ..."). | `DesktopGrpcEndpoint` re-binds the hosting URLs beside the h2c listener; the listener is Development-only unless `DesktopGrpc:Port` is set; it is exempt from HTTPS redirection. Verified live: 7203/5203/5204 all bound, h2c answers without redirect. |
| S2 | **Decrypted service-principal secret written back to the database in plaintext.** `GetSubscriptionInfo` assigned the plaintext to the tracked entity; the next `SaveChangesAsync` (audit rows of a deploy/stack/release) persisted it, after which every decrypt of that row failed. | Untracked read, plaintext on a copy. An unreadable stored secret now reads as "no subscription" (404) instead of an unhandled exception; re-saving replaces the row. |
| S3 | `SaveSubscriptionInfo` always inserted; lookups kept returning the first (stale) row after a secret rotation. | Upsert per (user, subscription); newest row wins on read. |
| S4 | `FindAsync(executionHash, token)` bound to `params object[]`: the token became a second key value, so decommission approve/execute always threw. | `FindAsync([executionHash], token)`. |
| S5 | Deployment monitor built `https://management.azure.comsubscriptions/...` for every scope but the tenant root. | `StagedDeploymentFamily.DeploymentsUrl`, tested. |
| S6 | An ARM deployment that **failed** crashed the apply path: the SDK throws from the status poll, the loop exits, `lro.Value` throws again - no terminal audit event, client stream dies. | Terminal failure captured and recorded as `Failed` with ARM's code. |
| S7 | Malformed object-id claim -> unhandled `FormatException` in 15 RPCs. | `GetUserId` returns null unless the claim is a GUID. |
| S8 | Design quota checked before the name lookup: at 10 designs you could not save changes to one you already had. `Load` threw on a missing design (protobuf setters reject null). | Quota gates creation only; missing design answers 404. |
| S9 | `GuardrailPolicyModel.Id` was an IDENTITY column on a database EF created, so the explicit `Id = 1` insert was refused. | `DatabaseGenerated(None)`. |
| S10 | `CryptoService` threw during DI construction with the placeholder vault name; `Encoding.Default` on decrypt; no RSA-OAEP size check. | Key loaded on first use (retried after a transient failure), UTF-8, size checked with a clear error. |
| S11 | Decommission window: scopes spliced into ARM URLs unvalidated; approver not compared with the executor; a truncated guardrail listing passed; cancellation recorded as a per-scope "failed"; raw exception text returned. | Scopes must be plain ids inside the subscription; approver must match; truncated listing fails closed; cancellation is outcome-unknown; clients get ARM's code, logs get the exception. |
| S12 | `UseHsts` ran before `UseForwardedHeaders`, so HSTS was never sent behind the ingress. | Order corrected. |

### Discovery / Workspace engines

| # | Defect | Fix |
|---|---|---|
| D1 | **Resource Graph inventory capped at 1,000 rows per query.** Paging continued only when `resultTruncated` was true - the opposite of the contract ("When resultTruncated is true, the $skipToken property isn't set", Learn: *Work with large data sets*). | Page on `$skipToken`; repeated token stops with `Truncated`. |
| D2 | **App-landed decommission locks could never be recognised or released.** Locks were PUT under a GUID, but an ARM lock's name is its last URL segment, and release looked for `ads-decom-...`. | Lock written and deleted by its window name; GUID id removed. |
| D3 | Guardrails and delete locks were judged for selected candidates only: a protected database was deletable by selecting its server. | Cascade children are judged; refusal lands on the parent. |
| D4 | Exposure analyzer missed the commonest internet-open rules: it read the empty plural `destinationPortRanges` ARM returns beside the filled singular, skipped `default-allow-ssh`/`-rdp` (what `az vm create` writes), only tested the first source, and matched ports as exact strings. | Singular+plural merged, numeric port ranges, every source, `::/0`, no name-based skip. |
| D5 | Scan checkpoint race with parallel groups: a checkpoint naming a group could commit before its rows (group lost on resume) or an older snapshot could overwrite a newer one (group duplicated). | One group at a time marks-and-persists; an unstored page is not marked complete. |
| D6 | Transport errors and HttpClient timeouts escaped the scan before the terminal record: the scan stayed "Running" forever. Cancelled token passed to the final write. | Recorded as that group's failure; terminal write uses a live token. |
| D7 | `Azure-AsyncOperation` monitors answer 200 `InProgress`; the first 200 was returned as the finished resource. | Polled to a terminal status, then the resource is read. |
| D8 | Deployment-operations parser read `targetResource`/`timestamp` at the entry root; ARM nests them under `properties` (Learn: *Deployment Operations - List*), so the per-resource monitor was always empty. | Reads `properties.*`, legacy shape still accepted. |
| D9 | `managedBy` is a top-level Resource Graph column; read from `properties`, every disk was "unattached". VM disks were matched by a `managedDisk.name` ARM does not send. | Column projected and folded into the row; disks linked by `managedDisk.id`, OS disk included. |
| D10 | PowerShell purge gate compared UTC "now" with a local-time `DateTime`: it opened early west of UTC. Runbook used `az lock create --lock/--scope`, `az resource exists`, `--id` - none exist (Learn: *az lock*, *az resource*). | `DateTimeOffset` on both sides; `--lock-type`, `--resource`, `az resource show --ids`, `--ids`. |
| D11 | `WorkspaceDecomWindows.LatestAsync` ordered by `DateTimeOffset` in SQLite (throws). Legacy importer deleted every source file before its single commit. | Ordered in memory; one design per transaction, file removed after its row is stored. |
| D12 | Culture-dependent parsing/formatting (cost cells, KQL timestamp), unbounded `nextLink` paging that would carry the token to another host, negative Retry-After delay, an exception filter that could never match, `microsoft.compute/loadbalancers`, case-sensitive guardrail tags. | Invariant culture; loop/host/page guards; clamped delay; filter fixed; correct type; tag keys matched case-insensitively. |

### Core (design model, export)

| # | Defect | Fix |
|---|---|---|
| C1 | **Anything inside a subnet (or any nested group) loaded at the canvas root**, and the next save made it permanent: child groups were never entered in the load-time lookup. | Child groups registered before attachment. |
| C2 | **Live-plane merge/refresh/detach stripped every type-specific setting** (address spaces, SKUs, runtime stacks) and saved the result: the clone was a base DTO. | Clone keeps the runtime type. |
| C3 | Function Apps exported as `"kind": "app"`; storage account named `astg` + app name (invalid with a hyphen or over 20 chars); container-published apps threw on export. | Kind follows the model; name sanitised (unchanged for names that were already valid); runtime stack read only for code publish. |
| C4 | Adopted resources loaded with null/0 for type-specific members: firewall and container instance threw, VNets lost their address space. Unknown types loaded stacked at the origin. | Re-typed from model defaults; unknown nodes keep position/size/lock/icon. Covered for every mapped ARM type. |
| C5 | FIX-01 allocator ignored subnets smaller than /24, capped covering subnets, skipped unparseable ones. | Range sweep over a strict `Ipv4Cidr`; checked against brute force on 3,000 generated layouts. |
| C6 | Required subnet names compared case-sensitively in placement/validity but not in the lookup. | One predicate (`DedicatedSubnets`); export writes the documented spelling. |
| C7 | Undo of a delete returned the item without its parent group (export then refused it). | Parent membership recorded on delete. |
| C8 | A failed load left `SuspendRefresh` on (canvas frozen). Two SQL servers crashed export on a duplicate parameter key. Hyphenated names failed every "lowercase" naming profile; the validator rejected the SQL stencil's own default name. | `finally`; `TryAdd`; `!IsUpper`; SQL rule per Azure's naming rules. |

### Client and Desktop

| # | Defect | Fix |
|---|---|---|
| U1 | **Pending offline designs deleted when the server refused the save** (400/507/500 arrive in the response body, not as exceptions). One un-syncable design made every later save/list throw. | Pending copy cleared only on 200; flush isolates failures per design. |
| U2 | **Loading a design cleared the canvas first**; a failed fetch or parse lost the unsaved work. Import said "replaced" but appended. | `CanvasLoader`: fetch/parse first, replace, restore the previous content on failure. |
| U3 | Picking a file to import always threw (synchronous read of a browser file stream). | Async read. |
| U4 | SVG/CSS numbers formatted with the UI culture (`x="12,5"` under pl-PL/de-DE): minimap, snap guides and topology view broke. | Invariant formatting. |
| U5 | Drag state machine: a second pointer orphaned the ghost; stale touch drags never cleared; rejected interop promises. | Any pointer-down clears; primary-button bit; `.catch`. 12 Node tests, now run in CI and no longer shipped in `wwwroot`. |
| U6 | "Loading the design ..." notice never dismissed; missing export icons refetched on every render; gallery entries with no file fetched the app's own page. | Keyed notice; attempt remembered; three icons added; clear message. |
| U7 | Deploy status callback was an `async void`; a broken stream left the dialog on "started". | Awaited `Func`; a lost stream is "unknown". |
| U8 | Desktop: failed resume lookup left the scan session "running" until restart; file logger unsynchronised; `CRED_PERSIST` constant was ENTERPRISE (roaming); stencil-load failure silent; resumed scans kept the oldest duplicate row. | All fixed. |
| U9 | Dev service worker never displaced a published one on the same origin (stale app). | `skipWaiting`, cache purge, `clients.claim`. |

## 2. Verification

See section 5 for the recorded results. Not verifiable from this machine, and therefore **not
claimed**: the GitHub workflows (CLI not signed in - they have been parsed and checked for
action refs, not run), the Docker image build (daemon not running), anything against live Azure,
and the rendered Desktop/WASM UI.

## 3. Improvement list

### 3.1 Executed in this pass

| ID | Improvement |
|---|---|
| I1 | Design validator reports invalid CIDR blocks, host-bit prefixes, subnets outside their network, and overlapping subnets. |
| I2 | Design validator reports duplicate resource names (scoped per network/server where Azure scopes them) and firewalls sharing a policy name. |
| I3 | New subnets - from the form and from the palette - get a free name and a free /24. |
| I4 | `Ipv4Cidr`: one strict IPv4 range type for allocation and validation. |
| I5 | `CanvasLoader`: one safe replace-the-canvas path for load, import and gallery. |
| I6 | JS drag tests run in CI; regression tests for every fix that has a test project (773 -> 1,024 offline tests). |
| I7 | AGENTS.md / README brought in line (Docker context, endpoints, CI set, three new pitfalls). |

### 3.2 Needs a decision from you

| ID | Proposal | Why it is yours to decide |
|---|---|---|
| P1 | **Application Insights key is hard-coded** in `wwwroot/index.html:38`, and the signed-in user's object id is sent with telemetry. Confirm the resource is yours, move it to configuration, or remove it. | Privacy / product. |
| P2 | Scan status `Partial` when a query group fails or a listing is truncated; today it is `Completed` and treated as authoritative. | Changes which scans count as "latest completed". |
| P3 | Delete the stale `src/Configurations/ads-stencils.json` and the now-redundant `Fix01SubnetAllocatorTests.cs` (superseded by `SubnetAllocatorTests.cs`; kept and corrected because deleting was not authorised). | Deletions. |
| P4 | Default firewall policy name per firewall (`<firewall>-policy`). | Renames an exported resource in existing designs. |
| P5 | `RetailPriceService` is anonymous with an unbounded, caller-keyed cache. Require sign-in or add input limits and a size-bounded cache. | Public pricing may be intentional. |

### 3.3 Needs live Azure before it can be called done

| ID | Proposal |
|---|---|
| P6 | **Deployment stacks cannot work as written.** `api-version=2021-04-01` is not a stacks version (2022-08-01-preview, 2024-03-01, 2025-07-01), and the body uses `properties.policy.actionOnUnmanage.{resourceGroup,deployment,missing}` / `denySettings.{status,locks}` where the API takes `properties.actionOnUnmanage.{resources,resourceGroups,managementGroups}` / `denySettings.{mode,applyToChildScopes,...}` (Learn: *Deployment Stacks - Create Or Update At Resource Group*). Needs a new policy model, wire contract and hash; the offline tests pin the invented shape. |
| P7 | ARM models that pass `IsValid` but Azure would reject or mis-deploy: Redis `sku.name = "C_1"`, Event Hubs/Service Bus `Tier = "Regional"`, DNS zone `location: ""`, App Gateway (capacity and autoscale together, no PIP/subnet), Private Endpoint (no subnet), Load Balancer frontend, bare VM/VMSS/ACI/AKS shells; and `WhenWritingDefault` dropping an explicit `false`/`0` (`clientAffinityEnabled`, APIM consumption capacity). |
| P8 | Run the `LiveAzure` test category; follow `properties.nextLink` in the Cost Management query. |

### 3.4 Belongs to an existing roadmap phase

| ID | Proposal | Phase |
|---|---|---|
| P9 | Role separation for approve / guardrail-admin / decommission; guardrail policy and approvals keyed per tenant + principal (today one global row any signed-in account can read or overwrite). | 5 |
| P10 | Server integration-test harness. None of the server fixes above has an automated test - there is no server test project. | 3 |
| P11 | EF migrations instead of `EnsureCreated` + hand DDL (and `EnsureCreated()` per request); a relational provider in Debug. | 4 |
| P12 | Offline delete tombstones; unique `(ScanId, ResourceId)` with upsert; bind a resume checkpoint to its request; scan hash over full rows. | 4 |
| P13 | Shared store for the what-if gate before replicas go above 1. | 6 |
| P14 | Envelope encryption or Key Vault references for subscription secrets. | 9 |

### 3.5 Engineering follow-ups

| ID | Proposal |
|---|---|
| P15 | Client test project (bUnit) for `DesignGrpcService`, `CanvasLoader` and the drawer flows. |
| P16 | Move GitHub Actions to current majors (checkout v7, setup-dotnet v6, upload-artifact v7, download-artifact v8, action-gh-release v3) - after one green run on the current ones, so a failure is attributable. |
| P17 | Supply the three gallery reference architectures (`hub-spoke` points at a missing file; two have no path). |
| P18 | Desktop sign-in: persist the MSAL token cache (device-code sessions are lost on restart), marshal the device-code prompt to the UI thread, interactive credential ahead of CLI/managed identity, refuse non-loopback `http://` server URLs. |
| P19 | Remaining unguarded gRPC awaits in drawers (stuck spinners), drawer stacking, raw-string `Tag` colours. |
| P20 | Core: report an unresolved `GroupId` on load instead of rooting the node; copy/paste shares `AddressSpace` instances; ARM state that survives regrouping (APIM, VM, OpenAI, AKS); shared plan SKU overwritten by a Function App; source-generator edge cases. |
| P21 | Engines: `ScanDiffer` empty shapes and tag-key case; `TopologyPositions`/`WorkspaceChanges` id casing; `ArmLockLevel.ReadOnly`; MCP cost summary that sums unit prices as a total. |
| P22 | Package drift: `Microsoft.Extensions.Logging.Debug` 10.0.0; client WebAssembly packages 10.0.12 vs Server 10.0.15. |

## 4. Plan

1. **Now (done):** section 1 and 3.1.
2. **Next, in this order:** commit this pass -> push and watch `build.yml` once (first evidence the
   workflows run) -> P16 -> P1/P3/P5 decisions -> P10 (so later server work is testable).
3. **Then, with an Azure subscription:** P8 -> P6 -> P7.
4. **Roadmap phases:** P9, P11-P14 in their phases; P15, P17-P22 as capacity allows.

## 5. Verification results

Run on Windows 11, SDK 10.0.401, 2026-11-12, against the working tree described above.

| Check | Result |
|---|---|
| `dotnet restore src/AzureDesignStudio.sln --locked-mode` | exit 0 (was failing on NU1903 before B1) |
| Lock files | only `AzureDesignStudio.Server/packages.lock.json` changed (3 entries) |
| `dotnet build src/AzureDesignStudio.sln -c Debug` (all 11 projects, incl. Desktop) | succeeded, 0 errors, 0 compiler warnings |
| CI project set in Release (`Server`, client with `-p:RunAOTCompilation=false`, `Mcp`, `Core.Tests`) | all succeeded |
| `dotnet test src/AzureDesignStudio.Core.Tests -c Release --filter "Category!=LiveAzure"` | **1,024 passed**, 0 failed (baseline before this pass: 773) |
| `node --test src/AzureDesignStudio/JsTests/*.test.cjs` | **12 passed** (baseline: 5) |
| `dotnet format --verify-no-changes` on the 71 changed/new C# files | exit 0 |
| `git diff --check` | exit 0 |
| Server run (`dotnet run --project src/AzureDesignStudio.Server`) | binds `https://localhost:7203`, `http://localhost:5203`, `http://localhost:5204`; HTTP redirects to HTTPS on 5203; HTTP/2 cleartext on 5204 answers 200 without redirect and 401 `Bearer` for an unauthenticated gRPC call |
| `GenerateVersioningTargets.ps1` with a stand-in nbgv output | generated targets import the root file; a build through it stamps `1.0.42+abc1234` |
| Web client in a browser (`dotnet run --project src/AzureDesignStudio`) | boots; a virtual network and two subnets dragged from the palette nest correctly, the minimap draws them, and Validate reports only the duplicate subnet name (no overlap: the two subnets received different ranges) |

Not verified here (see section 2): the three GitHub workflows end to end, the Docker image build,
any call against live Azure, service-worker behaviour (the test browser refuses to register any
service worker), the MAUI Desktop UI at runtime, and every server RPC fix - there is no server
test project, so those rest on reading the code plus the startup probe above.

Working-tree notes: `src/.dockerignore -> .dockerignore` is a staged rename (`git mv`); nothing else
is staged and nothing is committed. `Fix01SubnetAllocatorTests.cs` (untracked, from the 2026-10-30
pass) was kept and corrected rather than deleted; `SubnetAllocatorTests.cs` supersedes it.

## 6. Second pass (same day, "keep improving and fixing")

Items from section 3 that needed neither a decision nor live Azure. Still uncommitted, still on
top of `93dc172`. Section 5 is the record of the first pass; 6.3 is the record of this one.

### 6.1 Fixed in this pass

| Area | Was | Now |
|---|---|---|
| Server: resource-group lookups | `GetResourceGroupAsync` throws for a missing group, a rejected credential or a network failure; What-If, deploy and stack apply ended with an unhandled exception. | One `FindResourceGroupAsync` turns each into a status and message (404 / 403 / 502). `GetResourceGroups` likewise. |
| Server: stack apply | A transport or JSON failure after the PUT escaped as an RPC error - indistinguishable from "nothing happened". | Reported as outcome unknown (502), same as a lost poll. |
| Server: schema | `EnsureCreated()` ran in the constructor of two services - once per request. | Once at startup. (P11, in part.) |
| Server: retail prices | Caller-chosen strings went into the OData filter and into an unbounded cache key; `NextPageLink` was followed wherever it pointed; a truncated list was cached as complete. | Filter values and currency validated, cache capped at 2,000 entries, only `https://prices.azure.com/` links followed, truncated results not cached. (P5, in part - the endpoint is still anonymous; that remains your call.) |
| Server: linking a subscription | A missing Key Vault answered 400, i.e. "your details are wrong". | 400 for bad input only; 503 when the server cannot store the secret. |
| Engines: scan resume | A checkpoint was resumed for whatever request came next, so a scan of other subscriptions could continue from another scan's page. | Checkpoint carries a request key; a mismatch is refused. Subscriptions are ordered before chunking and group keys are case-insensitive, so the same request always produces the same groups. (P12, in part.) |
| Engines: drift | Empty `{}` / `[]` vanished from the flattened view, so "became empty" was no change; tag keys differing only in case showed as removed + added. | Empty shapes are leaves; tag keys compared case-insensitively. (P21) |
| Engines: decommission | `ReadOnly` locks were not treated as blocking a delete. | They are. (P21) |
| Engines: actual spend | Only the first page of a Cost Management query was read. | Follows `properties.nextLink` (same host, 200-page cap). (P8, in part.) |
| Workspace | "Latest completed scan" could pair one scan's header with another's snapshots; topology positions were keyed case-sensitively, so one resource could hold two positions. | Scan chosen first, then its snapshots; positions saved and read case-insensitively, newest wins. (P21) |
| MCP | Cost summary presented a sum of unit prices as a total. | Says what the figure is. (P21) |
| ARM export: Redis | `sku` at the top level with name `C_1`. | `properties.sku` with `Basic/Standard/Premium`, family and capacity; capacity limits per family in validation and in the form. (P7) |
| ARM export: Event Hubs, Service Bus | `tier` was a fixed value unrelated to the SKU name. | Tier follows the name. (P7) |
| ARM export: DNS zone, Traffic Manager | Empty or regional `location`; Traffic Manager without `monitorConfig` or a DNS name. | `global`; default HTTPS/443 monitor; relative DNS name from the profile name. (P7) |
| ARM export: storage account | The form was a stub; any SKU/kind pair exported. | Real form; premium-only kinds require a premium SKU (validation). (P7) |
| ARM export: explicit `false` / `0` | `clientAffinityEnabled: false` and APIM Consumption `capacity: 0` were dropped by the serializer, so Azure applied its own default. | Written. (P7) |
| Core: stale export state | APIM kept its network configuration after leaving a subnet; a VM kept its availability set; the OpenAI subdomain and AKS DNS prefix were frozen at the first name. | Cleared / derived at each export. (P20) |
| Core: copy and paste | A pasted virtual network shared its `AddressSpace` objects with the original - editing one edited both. | Copied. (P20) |
| Core: validation | A web app on a plan that hosts a Consumption / Elastic Premium function app exported without complaint. | Placement error. (P20) |
| Core + client: load | An item whose parent group was missing from the file was put on the canvas root without a word. | Still loaded there (nothing is dropped), and the load/import says which items. (P20) |
| Client: server calls | Any transport failure in the save, user, subscription or deploy drawers threw out of the handler and left the spinner on for good; a failed save said "Error code: 503". | The service wrappers answer with a status; each drawer says what failed in words. (P19, in part.) |
| Client: linked subscriptions | "Could not ask the server" looked like "none linked" and offered the link form again. | A warning instead. |
| Client: linking a subscription | A refused link stopped the spinner and said nothing. | Says why. |
| Client: save drawer | At 10 designs the Save button locked - also for saving changes to an existing design, which the server allows. After a delete the row stayed selected and the count was stale. | Locked for new names only; selection and count refreshed; a list that cannot be read no longer shows as an empty table. |
| Desktop: device-code sign-in | MSAL calls the prompt from a worker thread; setting the window title there throws, so the fallback sign-in failed before showing a code. The code was also written to the log file. | Marshalled to the UI thread; the code is shown, not logged. (P18, in part.) |
| Desktop: bearer token | Sent to whatever `ServerUrl` named, including a remote `http://` host. | Sent over https or to loopback only; a config note is written at startup otherwise. (P18, in part.) |

### 6.2 Status of the section 3 list after this pass

Done: P21. Done except for what needs you or Azure: P5 (anonymity is the open part), P7 (App
Gateway, Private Endpoint, Load Balancer and the bare compute shells are untouched - they need a
design decision on defaults and a live deployment to confirm), P8 (the `LiveAzure` run), P18
(token-cache persistence and credential order), P19 (drawer stacking, `Tag` colours), P20 (shared
plan SKU, source-generator edge cases). Unchanged: P1-P4, P6, P9-P17, P22.

### 6.3 Verification results (this pass)

| Check | Result |
|---|---|
| `dotnet restore src/AzureDesignStudio.sln --locked-mode` | exit 0 |
| `dotnet build src/AzureDesignStudio.sln` (Debug, all 11 projects) | succeeded, 0 errors |
| CI project set in Release | all succeeded |
| `dotnet test ... -c Release --filter "Category!=LiveAzure"` | **1,068 passed**, 0 failed (first pass: 1,024) |
| `node --test src/AzureDesignStudio/JsTests/*.test.cjs` | 12 passed |
| `dotnet format --verify-no-changes` on the 99 changed/new C# files | exit 0 |
| `git diff --check` | exit 0 |

New tests: `EngineRegressionTests`, `ArmModelShapeTests`, `ModelStateTests`.

Not covered by any test, verified by build and reading only: every server change in 6.1, the
client drawer and service-wrapper changes, and both Desktop changes - there is still no server or
client test project (P10, P15), and the Desktop UI was not run. The ARM shapes were checked
against the Microsoft Learn template references, not against a deployment.

## 7. Third pass (same day)

More of section 3, again limited to what needs neither a decision nor live Azure. Uncommitted,
on top of `93dc172`.

### 7.1 Done in this pass

| Area | Was | Now |
|---|---|---|
| Server tests (P10) | No test project: every server fix in sections 1 and 6 rested on reading the code. | `src/AzureDesignStudio.Server.Tests` - 59 tests that call the gRPC service classes directly (InMemory EF, a fake `ServerCallContext`, a fake Key Vault, scripted HTTP). Covers identity handling (missing / malformed / short `oid` claim), design save-load-delete, the quota fix, per-user isolation, subscription linking (encrypt, upsert, 400 vs 503), the unreadable-link 404, the guardrail policy, the Desktop listener's port and address resolution, the Key Vault placeholder, and the retail-price proxy (paging host, cache, input limits, OData quoting). In the solution, in `build.yml`, in `.dockerignore`, in AGENTS.md. |
| Packages (P22) | Client ASP.NET Core packages on 10.0.12 beside a 10.0.15 server package; `Microsoft.Extensions.Logging.Debug` on 10.0.0. | Client ASP.NET Core packages and the `Microsoft.Extensions.*` family on 10.0.15 (the two move together - NuGet refuses the mix). Seven lock files regenerated. EF Core stays on 10.0.12. |
| App Service plan (P20) | The plan form had only name and location. A Function App on dedicated hosting forced the plan to P1V2 on every export, overwriting whatever the plan held - also for web apps sharing it. | The form offers the dedicated sizes (B1-P3v3); a dedicated Function App keeps the plan's size; an Elastic Premium size already on the plan is kept; a serverless SKU left on a plan with no serverless Function App is replaced; while a Function App uses Consumption or Premium the form says the size follows it. |
| Scan storage (P12, in part) | A resumed scan re-delivers the pages of the query group it was killed in; they were appended, so those resources were in the scan twice and the count was inflated. | One row per resource per scan, later page wins, count follows. Readers still de-duplicate because scans stored earlier can hold duplicates. |
| Drawers (P19) | Switching from one drawer to another ran the old drawer's close handlers against the new one: its reference was dropped, so the next switch could not close it (drawers piled up), and its name was cleared, so its menu item opened a second copy. The tenant-scan drawer opened over whatever was already open. | Close handlers act only for the drawer that is still current; the scan drawer closes the open one first. |

### 7.2 Looked at and deliberately not changed

- **Offline delete tombstones (P12).** A design deleted offline comes back from the server on the
  next sync. A tombstone that is replayed later would also delete a design of the same name that
  was re-created elsewhere in the meantime - the server keeps no version or timestamp the client
  could compare. Fixing one data-loss case by adding another is not an improvement; this needs the
  phase 4 sync design.
- **`Tag` colours as strings (P19).** Not a defect: AntDesign resolves the preset names. It is a
  convention mismatch across ~60 call sites, most of them in the Desktop scan drawer that cannot
  be run here. Left alone.
- **MSAL token-cache persistence (P18).** Needs a new dependency and a run of the Desktop app to
  confirm; neither is possible in this environment.

### 7.3 Status of the section 3 list

Done: P10 (service-level; a hosted end-to-end harness with real auth middleware is still phase 3
work), P21, P22. Open parts: P5 (anonymity), P7 (App Gateway, Private Endpoint, Load Balancer,
bare compute shells), P8 (`LiveAzure` run), P12 (tombstones, unique index, hash), P18 (token cache,
credential order), P20 (source-generator edge cases). Unchanged: P1-P4, P6, P9, P11, P13-P17.

### 7.4 Verification results (this pass)

| Check | Result |
|---|---|
| `dotnet restore src/AzureDesignStudio.sln --locked-mode` | exit 0 |
| `dotnet build src/AzureDesignStudio.sln` (Debug, all 12 projects) | succeeded, 0 errors |
| CI project set in Release, incl. `Server.Tests` | all succeeded |
| `dotnet test src/AzureDesignStudio.Core.Tests -c Release --filter "Category!=LiveAzure"` | **1,075 passed**, 0 failed |
| `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false` | **59 passed**, 0 failed |
| `node --test src/AzureDesignStudio/JsTests/*.test.cjs` | 12 passed |
| `dotnet format --verify-no-changes` on the 104 changed/new C# files | exit 0 |
| `git diff --check` | exit 0 |
| Server start on the updated packages | listens on 7203; serves the app page, `blazor.webassembly.js` and the stencil catalogue (200) |
| Web client in a browser on the updated packages | boots and shows the palette; the only console errors are the service-worker registration this test browser always refuses |

Still not verified: the workflows on GitHub (the new test step included), the Docker image build,
anything against live Azure, the Desktop app at runtime, and the client drawer changes beyond
"builds and the app boots" - there is no client test project (P15). The new lock file
`src/AzureDesignStudio.Server.Tests/packages.lock.json` must be committed with the project: the
workflow fails when a project has no tracked lock file.

## 8. Fourth pass (same day)

Uncommitted, on top of `93dc172`.

### 8.1 Done in this pass

| Area | Was | Now |
|---|---|---|
| Gallery (P17) | "Hub Spoke Topology" pointed at `gallery/hub-spoke-final.json`, which was never in the repository (a failed fetch and a JSON error); the other two entries had no design. | Three designs under `wwwroot/gallery`, generated from the app's own models by builders in `Core.Tests/GalleryDesignTests.cs`: every node is placed through its real drop rule and every link through the real port rule. Tests fail when a gallery entry has no file, when a file does not load cleanly, validate without errors and export, or when a file no longer matches its builder. Regenerate with `ADS_WRITE_GALLERY`. |
| Frames on the canvas | Found while checking the gallery in a browser: every frame (virtual network, subnet, App Service plan, ...) was painted over by its own icon stretched to the frame's size, with no outline, no background and the title unplaced. The frame stylesheet selected `.group.ads-group` - one element with both classes, which is how the old diagram fork rendered a group. Since the move to the Z.Blazor.Diagrams package the library's wrapper and our element are two elements, so nothing matched. | Selectors target our element; the icon is a 24 px corner badge; a selected frame is outlined; a model's own colour (the plan's tier tint) is mixed into the surface so the title stays readable in both themes. Committed code, so this affected every design with a frame. |
| Loading a design | The canvas kept the pan and zoom of whatever was on it before, so a loaded, imported or gallery design could appear partly or wholly off screen. | Fitted and centred after load, never magnified past 100%. |
| Image export / print | The page was centred with `UpdatePan`, which adds to the current pan: a canvas the user had scrolled exported off-centre by that scroll. | `SetPan`. |
| Client wrapper tests (P15, in part) | No coverage of the client's gRPC wrappers - the code that decides whether an offline save is kept or deleted. | 30 tests in `Server.Tests` (which already reaches the client assembly): failed calls become statuses on the web client; a save the server cannot take is kept locally; a refused save never clears the only copy; one faulting design does not stop a flush or fail the call that started it; local edits win over the server copy; "could not ask" is distinct from "none linked" / "no resource groups"; a deployment stream lost midway is reported as unknown, one that never started as an error. |

### 8.2 Finding for P1 (your decision, now with evidence)

The hard-coded Application Insights key in `wwwroot/index.html` is not only a privacy question: the
ingestion endpoint **rejects it**. Every page load posts to
`https://dc.services.visualstudio.com/v2/track` and gets `400`. Telemetry is not being collected.
Either supply a working connection string through configuration or remove the snippet.

*Correction (section 9):* an earlier wording here counted "29 console errors in one short session"
against telemetry. Most of those were the test browser refusing to register the service worker,
which it does for any page. What was actually observed for telemetry is one rejected `track`
request per page load.

### 8.3 Status of the section 3 list

Done: P10 (service-level), P17, P21, P22. Open parts: P5 (anonymity), P7 (App Gateway, Private
Endpoint, Load Balancer, bare compute shells), P8 (`LiveAzure` run), P12 (tombstones, unique index,
hash), P15 (drawer and `CanvasLoader` tests need a Blazor component test host), P18 (token cache,
credential order), P20 (source-generator edge cases). Unchanged: P1-P4, P6, P9, P11, P13, P14, P16.

### 8.4 Verification results (this pass)

| Check | Result |
|---|---|
| `dotnet restore src/AzureDesignStudio.sln --locked-mode` | exit 0 |
| `dotnet build src/AzureDesignStudio.sln` (Debug, all 12 projects) | succeeded, 0 errors |
| CI project set in Release, incl. `Server.Tests` | all succeeded |
| `dotnet test src/AzureDesignStudio.Core.Tests -c Release --filter "Category!=LiveAzure"` | **1,085 passed**, 0 failed |
| `dotnet test src/AzureDesignStudio.Server.Tests -c Release -p:RunAOTCompilation=false` | **89 passed**, 0 failed |
| `node --test src/AzureDesignStudio/JsTests/*.test.cjs` | 12 passed |
| `dotnet format --verify-no-changes` on the 106 changed/new C# files | exit 0 |
| `git diff --check` | exit 0 |
| Web client in a browser | each of the three gallery tiles, dragged onto the canvas, loads its design ("The design is loaded"), fitted to the view; frames show outline, title and corner icon; links are labelled (Peering, Public IP) |

Not verified: the frame styles and fit-to-view in the Desktop app (same components, not run); image
export after the centring fix (changed by reading the library's `SetPan` / `UpdatePan`, not by
producing an image); light theme (only the dark theme was looked at); and, as before, the GitHub
workflows, the Docker build and live Azure.

## 9. Client telemetry removed (decision on P1: "Remove it")

| Was | Now |
|---|---|
| `wwwroot/index.html` carried the Application Insights snippet with a hard-coded key: it loaded a script from `js.monitor.azure.com` and posted to `dc.services.visualstudio.com` on every page load, and Azure answered `400`. `Index.razor` sent the signed-in user's object id as the authenticated-user context, and the logging provider forwarded "User logged in. User Id: ..." as a trace. The Desktop app loads the same `index.html`, so its `EnableTelemetry = false` switch (which only left out one component) never stopped the snippet. | Gone from the client: the snippet, the `BlazorApplicationInsights` interop script, `<ApplicationInsightsComponent />`, the `EnableTelemetry` parameter and the Desktop line that set it, the user-context calls, the service registration in the web client and in Desktop, the logging-provider section in `wwwroot/appsettings.json`, the package reference and its central version. The sign-in log line no longer contains the user id. |

Left as it was: server-side telemetry (`AddApplicationInsightsTelemetry()` in the Server). It is
driven by `ApplicationInsights:ConnectionString`, which is a placeholder in the checked-in
settings, so it is the deployer's choice and off by default.

Verification: the solution builds (all 12 projects, Desktop included); locked restore passes with
the regenerated lock files, none of which mentions the package any more; Release CI set builds;
1,085 + 89 + 12 tests pass; format gate and `git diff --check` pass. In a browser the web client
boots, `window.appInsights` is undefined, no request leaves `localhost`, and no resource fails.
Not verified: the Desktop app at runtime - it compiles, and removing a parameter assignment for a
parameter that no longer exists is what keeps it from failing at start-up, but it was not launched.

P1 is closed. README, `docs/architecture.md` and AGENTS.md say that the client sends nothing.

## 10. Finishing pass, checked against Azure (2026-11-13)

Request: finish the app, test it end to end, push. Azure checks were limited by agreement to
**validate-only** calls (`az deployment group validate`, `what-if`, the deployment-stacks
`validate` endpoint) against an existing resource group: nothing was created, changed or billed.

### 10.1 Fixed

| Area | Was | Now |
|---|---|---|
| Deployment stacks (P6) | api-version `2021-04-01` (not a stacks version) and a `properties.policy` wrapper with `missing`/`resourceGroup`/`deployment` actions and a `status` + `locks[]` deny model - none of which exists in the API. Every stack apply would have been refused. | The documented 2024-03-01 body: `actionOnUnmanage.{resources,resourceGroups,managementGroups}` (`detach`/`delete`; delete still refused until Phase 13) and the required `denySettings.{mode,applyToChildScopes}`; no name/type/location in the body. The plan hash binds body + stack name. Proto fields renamed/reserved accordingly. **Azure's stack `validate` endpoint accepts all three gallery stacks (HTTP 200, 21/9/9 resources validated).** |
| Virtual machine | Exported a name and nothing else - no size, image, OS profile or network. | NIC in its subnet (or its availability set's subnet), size, image picker (Ubuntu 22.04/24.04, Debian 12, Windows Server 2022), OS-specific computer name, admin user, password as the secure template parameter `adminPassword` (never stored). Accelerated networking where the size supports it. Default size `Standard_D2s_v6`: `Standard_B2s` was capacity-restricted in the test region, and the Bsv2 family had no quota there. |
| VM scale set | No `virtualMachineProfile` at all; could not be placed in a subnet. | Full machine profile, NIC configuration in its subnet, manual upgrade policy; placed in a subnet. |
| Container instance | Empty `containers` list; CPU/memory written on the group, where the API has no such members. | One container (image field, default the ACI hello-world image) with its CPU/memory as resource requests; restart policy. |
| Private endpoint | No subnet, empty target. | In a subnet; target from a link to a resource on the canvas (group id derived from its type) or a typed id. |
| Load balancer | Frontend referencing nothing (`FrontendIPConfigurationHasNoSubnetOrPublicIPAddressOrPublicIPPrefix`). | Public via a linked public IP, or internal in its subnet. |
| ExpressRoute | SKU name `Standard50Mbps` (not a value - `InvalidJsonResourceType`); `serviceProvider` string the API does not have. | `{tier}_{family}` SKU, `serviceProviderProperties { serviceProviderName, peeringLocation, bandwidthInMbps }`; form rebuilt. |
| Application Gateway | Capacity *and* autoscale written together (`...MustSpecifyEitherCapacityOrAutoscaleConfiguration`); no gateway IP config, frontend, port, pool, settings, listener or rule. | One or the other; the full listener -> rule -> pool chain on HTTP/80; WAF configuration for the WAF tier; requires its subnet and a linked public IP (link from either end). |
| AKS | No identity or service principal (`servicePrincipalProfile is missing`); pool written as `defaultAgentPoolProfile`, which is not a member. | System-assigned identity; `agentPoolProfiles[]`; placed in a subnet it uses Azure CNI with a non-overlapping service range. |
| Subnets | Siblings in one network were written in parallel - Azure refuses that with `AnotherOperationInProgress` at deployment (validation does not show it). | Each subnet depends on the one before. |
| Adopt engine | Read each model's ARM type back from a throwaway export, which needed hand-built parents and dropped any model whose export rules grew stricter. | Models state their ARM type (`ArmResourceType`); the probe and its scaffolding are gone. |
| Scans (P2) | A scan with failed query groups or a truncated listing was `Completed` and became the drift baseline - everything it missed looked deleted. | `Partial`: browsable, never the baseline (store and Desktop rescan diff take `Completed` only). |
| Docker image / Linux restore | **Found by building the image:** `NU1403` - the `wasm-tools` workload's local copy of `Microsoft.NET.Sdk.WebAssembly.Pack` hashes differently on Windows, Linux and nuget.org, so the lock file written on Windows failed on Linux. The Docker build and the publish workflow's Linux job could never have restored. | The client sets `DisableImplicitLibraryPacksFolder=true`; every machine takes nuget.org's package. On this machine the workload-sourced cache entry was moved into the session scratchpad (not deleted) so the lock records nuget.org's hash. |

### 10.2 Verification (this pass)

| Check | Result |
|---|---|
| `az deployment group validate`, every palette entry's minimal design + the 3 gallery designs | **49 of 49 pass** (first run: 42 of 46) |
| `az deployment group what-if`, same 49 | **49 of 49 pass** |
| Deployment-stacks `validate` endpoint, 3 gallery stacks | **3 of 3 pass** (HTTP 200) |
| `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"` | **1,164 passed**, 0 failed |
| `dotnet test src/AzureDesignStudio.Server.Tests` | 89 passed |
| Desktop app (Debug) | starts, opens its window, logs no warning or error in 30 s, closes cleanly |
| `dotnet format --verify-no-changes` (133 changed C# files), `git diff --check` | exit 0 |

Docker image and full CI-set results: see 10.3. Server tests are 95 after 10.3's additions.

What validate/what-if do not prove: quota and capacity at deployment time (they surfaced here
only because validation happens to check some of them), resources' runtime behaviour, and the
Desktop UI beyond start-up (no UI automation was available).

### 10.3 Image and final run

| Area | Was | Now |
|---|---|---|
| Server start-up in Release (the image) | **Found by running the image:** it crashed on start with `UriFormatException` - the vault URL was built from the placeholder `<kv name>`. Debug never runs that path. And with no vault there was no way to give the server a database at all. | Placeholders count as unset (`ServiceTools.IsConfigured`, shared with `CryptoService`). The connection string comes from the vault's `ads-main` secret or from `ConnectionStrings__ads-main`; with neither, start-up stops with a message naming both. `Database__Provider=InMemory` starts it without a database for smoke tests (a warning is logged). |

| Check | Result |
|---|---|
| `docker build -f src/AzureDesignStudio.Server/Dockerfile .` (full WebAssembly AOT) | succeeds (failed with NU1403 before the library-packs fix) |
| Image, nothing configured | stops with "No database is configured ..." (crashed with `UriFormatException` before) |
| Image, `Database__Provider=InMemory` | serves the app, `blazor.webassembly.js`, the stencil catalogue and the gallery (200); the AOT-compiled client loads in a browser and a gallery design drops onto the canvas |
| Locked restore, solution build (Debug, 12 projects), CI Release set | pass |
| Core / Server / JS tests | 1,164 / 95 / 12 passed |
| Format gate (133 files), `git diff --check` | exit 0 |

### 10.4 Left open

- **Not done, needs your decision:** deleting `src/Configurations/ads-stencils.json` (stale copy) and
  `Fix01SubnetAllocatorTests.cs` (superseded); a per-firewall default policy name (P4 - would rename
  policies in existing designs; the validator already reports two firewalls sharing one).
- **Kept as is:** the retail-price endpoint stays anonymous (P5) - public data, now bounded.
- **Roadmap work, not started:** P9 role separation, P11 migrations, P12 offline delete tombstones,
  P13 shared what-if gate store before more than one replica, P14 envelope encryption, P15 component
  tests for the drawers, P16 action major-version bumps, P18 MSAL token-cache persistence.
- **Not committed:** `.planning/.omp-*.json` and `.planning/milestone.lock` (tool runtime state) and
  `.planning/audits/2026-10-30/evidence/offline-tests.trx` (about 1,500 local machine paths with the
  user name) - on disk, untouched.
- **Not verified:** an actual deployment to Azure (validate/what-if only, by agreement), the Desktop UI
  beyond start-up, the GitHub workflows until this push runs them.

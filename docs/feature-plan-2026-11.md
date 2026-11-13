# Feature plan, November 2026: what to build next

**Date:** 2026-11-13 · **Status:** proposal, not yet approved · **Does not change** `.planning/ROADMAP.md` or `STATE.md`

This plan comes from two web-research passes (competing tools; Azure platform changes in 2025-2026) checked against what the repo already has and what `docs/v3-plan.md` (N1-N50) already schedules. Items that already exist in v3 keep their N number; the rest are new.

Sizes use the repo's scale: **S** up to 2 days, **M** 3-5 days, **L** 8 days or more.

## 1. What the research found

### Competing tools

| What they market | Who | Where ADS stands |
|---|---|---|
| Terraform/OpenTofu from the diagram | Brainboard (free tier), Azure Copilot Deployment Agent, Massdriver | Only for scans (`Discovery/Iac/TerraformText.cs`, AzAPI); not for a design |
| Prompt to architecture, chat refinement | Azure Architecture Diagram Builder, Eraser, Copilot Deployment Agent, Brainboard | None. MCP server is read-only |
| Well-Architected scoring of a design | Azure Architecture Diagram Builder, Copilot Deployment Agent | None. Offline linter is planned (POST-03) but last in the queue |
| Generated documents and audit evidence (Word/PDF) | Cloudockit, Lucid | HTML report and BOM for scans (`Discovery/Report/`); nothing for a design |
| Export to Visio/draw.io, embeds | Cloudockit, Hava | Image export only; N45 planned |
| Version history with visual diff | Hava, Brainboard | Compare drawer exists; design history is N27 |
| Cost/security overlays, tag filters on live topology | Cloudcraft, Hava | Exposure and cost exist; no Advisor/policy/attack-path overlay |
| Git/PR flow, CLI, CI action | Brainboard, Hava, Copilot Deployment Agent | N43/N44 planned |
| MCP tools that create things | Eraser, Azure Architecture Diagram Builder, draw.io | Read-only by design; MCP-01 "propose" tier deferred |
| Multi-cloud, real-time collaboration | Most commercial tools | Out of scope by decision (PROJECT.md) |

The closest direct competitor is the open-source **Azure Architecture Diagram Builder**, which in July 2026 became an MCP server with Well-Architected validation, Retail Prices estimates and Bicep generation. It has no tenant scan, drift or gated deploy. Microsoft's **Copilot Deployment Agent** generates Bicep/Terraform and opens PRs but handles greenfield only and exposes no API.

ADS's edge is unchanged: live tenant truth plus gated execution, offline, on the desktop. The gaps are at the two ends, the design review before deploy and the ways a design leaves the app.

### Azure platform changes that affect ADS

| Change | Status | Effect on ADS |
|---|---|---|
| Default outbound access off for new VNets (API versions after 31 Mar 2026) | In force | ADS exports subnets at `2022-07-01`, which keeps the old default, so exports work today but depend on a behaviour Azure is retiring. The property is not written, and a NAT gateway on the canvas is never attached to its subnet in the template |
| Deployment stacks api `2025-07-01`; stacks what-if | GA | ADS writes `2024-03-01` and uses plain what-if, which does not show unmanage/delete or deny-assignment effects |
| `checkPolicyRestrictions` (`Microsoft.PolicyInsights`, `2024-10-01`) | GA | The API for N33 policy preflight. Reported to miss management-group-inherited policies |
| `.bicepparam`, `bicep snapshot` (CLI 0.41.2+) | GA | Offline predicted-resources diff; needs N30 first |
| Azure Verified Modules; ALZ Bicep is now AVM-only | Active | Confirms N31 |
| ARG tables `advisorresources`, `policyresources`, `securityresources` (attack paths) | GA | Overlays using the scan client ADS already has. Not used today |
| Retirements: App Gateway v1, Basic public IP and Basic LB, Front Door classic, CDN classic | Past or dated | `Analysis/DeprecationCatalog.cs` covers classic resources only |
| Infrastructure Resiliency Manager; Service Groups; Carbon Optimization API | Preview | Zone-support matrix can seed an offline rule set now; the APIs can wait |
| AVNM IPAM pools, network verifier | GA (limited regions) | Later option for CIDR allocation and reachability cross-check |
| Azure MCP Server (pricing, policy, advisor, WAF, deploy namespaces) | GA | Complement it; do not duplicate live-Azure tools in the ADS MCP server |

## 2. Proposed order

### Wave A: keep exports correct (do first) - DONE

A1, A2 and A3 are implemented on `feat/wave-a-export-correctness` (see the table below). The first live run of the stacks what-if still needs a subscription where the caller has `deploymentStacksWhatIfResults/write`; do it by hand from the Desktop app before release.

Azure moved under the exporter. These are small and protect the claim that an exported template deploys.

| # | Item | Size | Where |
|---|---|---|---|
| A1 (done) | Write `defaultOutboundAccess` explicitly on exported subnets; validation rule for a VM/VMSS subnet with no outbound path. A NAT gateway (linked to a machine in the subnet, with a linked public IP) is the only modelled outbound path; a firewall in the same or a peered network downgrades the warning to Info. LB outbound rules, NIC public IPs and route tables are not modelled | S-M | `Core/Network/`, `ValidationDrawerTemplate`, golden templates |
| A2 (done) | Extend `DeprecationCatalog` from resource types to SKUs. Basic public IP, Basic load balancer and Application Gateway v1 are refused in designs and flagged in scans; Front Door classic and CDN Standard from Microsoft (classic) are flagged in scans as "review due" with no date | S | `Discovery/Analysis/DeprecationCatalog.cs` |
| A3 (done) | Stacks use api-version `2025-07-01` with an unchanged body (no `validationLevel`, no `resourcesWithoutDeleteSupport`), so existing plan hashes and approvals stay valid; stack applies require a token from the stacks what-if | M | `Discovery/Iac/DeploymentStack.cs`, `Services/DeployService` |

Decisions made during implementation:

- (a) A NAT gateway serves a subnet through a link to a node inside it, because subnets have no ports.
- (b) An indeterminate stack preview issues no token.
- (c) While a guardrail policy has tag rules, a stack preview or apply with any modify/detach/delete row is blocked, because stack what-if rows carry no tags.
- (d) Scans now capture the top-level SKU, and drift ignores a SKU only the newer scan has.
- (e) A design holding a NAT gateway with no public IP now fails validation (ARM refused that export before).

Check before building: open each retirement page for the exact date (the research has several from search snippets only); confirm the stacks what-if api-version against the REST reference.

### Wave B: review a design before it is deployed

This is what competitors now lead with, and ADS can do it more honestly because it also has the live tenant.

| # | Item | Size | Notes |
|---|---|---|---|
| B1 | Offline architecture linter, grouped by Well-Architected pillar (POST-03 pulled forward). First rule sets: A1/A2, zone redundancy from the resiliency support matrix, public exposure, missing private endpoints, missing diagnostics | L | **Shipped.** Deterministic rules only, each with a Learn link. Same results in the validation drawer and one MCP tool (`ads_design_lint`). See "B1 as shipped" below |
| B2 | Policy preflight per node via `checkPolicyRestrictions` (N33) | M | State the management-group limitation in the UI; what-if stays the authority |
| B3 | Advisor, policy-compliance and attack-path overlays on the scanned topology from the three ARG tables | M | New scan query groups; a missing Defender plan must show as "no data", not "no findings" |
| B4 | Capacity and quota preflight (N34) | M | Unchanged from v3 |

### Wave C: more ways out to infrastructure as code

| # | Item | Size | Notes |
|---|---|---|---|
| C1 | Terraform (AzAPI) export from a design | M | Reuse `TerraformText`; AzAPI bodies are the ARM bodies ADS already generates. AzureRM provider is a non-goal |
| C2 | `.bicepparam` and environment overlays (N30), then `bicep snapshot` as an offline diff between a design and its last export | M | `BicepParamsText` exists for scans |
| C3 | Azure Verified Modules as an output target (N31) | M | Start with the resource types that have a stable AVM module |
| C4 | Generic ARM tile (N25), then ARM JSON and Bicep import to the canvas (N29) | L + L | N25 is the prerequisite; unsupported constructs are counted, never dropped |

### Wave D: share designs and let agents work on them

| # | Item | Size | Notes |
|---|---|---|---|
| D1 | Design document export: diagram, resource list, cost estimate, linter findings and naming, as Markdown and PDF/Word | M | Extend `Discovery/Report/`; the linter (B1) makes it worth reading |
| D2 | MCP "design" tools: create and edit a design in the workspace, lint it, estimate cost, export Bicep/Terraform. Never touch Azure (MCP-01 narrowed) | M-L | This is the AI answer: any MCP client becomes the assistant, with no model or key inside the app. Needs a second, writable connection limited to design tables |
| D3 | draw.io export (N45). Visio only if someone asks | M | |
| D4 | Design history with a visual diff between two versions (N27) | M | Compare drawer already diffs design against scan |
| D5 | Headless CLI and a GitHub Action for lint, drift and BOM (N43/N44) | M + M | The CLI also gives B1 a pipeline use |

### B1 as shipped

Ten rules in `Core/Validation/DesignLinter.cs`: REL-VM-SINGLE, REL-AVSET-ONE, REL-APPPLAN-TIER, REL-APPGW-INSTANCES, REL-SQL-BASIC, REL-STORAGE-LRS, SEC-APPGW-NO-WAF, SEC-NO-PRIVATE-ENDPOINT, SEC-NO-BASTION, OPS-NO-MONITORING.

Scope decisions:
- Rules use only data the design models hold. There are no zone, TLS, diagnostics or NSG rules until the models carry that data (the models carry almost no zone data; `ContainerAppsEnvironmentModel.ZoneRedundant` is the exception).
- No rule duplicates a `DesignValidator` rule: the validator reports issues (errors block deployment), the linter only advises.
- No Performance or Cost rules yet.
- Tenant-scan linting is not included; the linter runs on designs only.
- The linter never returns Error severity (Warning and Info only).
- The bundled gallery designs currently produce Warnings: hub-spoke (two single VMs outside an availability set), aks-baseline and apim-landingzone (Standard_v2 application gateway without WAF). `GalleryDesigns_LintFindingsArePinned` pins them.
- The MCP tool is read-only: it loads a saved design from the workspace and never contacts Azure.

## 3. Not proposed

- **Multi-cloud, real-time collaboration, a home-grown policy engine:** already out of scope, and nothing in the research changes that.
- **A chat model embedded in the app:** needs a key or a hosted service, breaks the offline and no-telemetry stance, and D2 gets most of the value. Open for decision below.
- **Agent-run remediation (Firefly, System Initiative style):** contradicts the hashed-plan, named-approval rule.
- **Carbon, Service Groups, Resiliency Manager APIs:** preview; revisit when GA.
- **Copilot agents:** no third-party API exists.

## 4. Things to settle before this is scheduled

1. **The roadmap predates the standalone desktop.** Phases 3, 5, 6 and 9 in `ROADMAP.md` harden the gRPC server, and since `a115b90` the desktop needs no server and the server gets no new work. The trust gate should be re-scoped to the desktop (migrations, backup, signed MSIX, notarized .dmg, E2E) before these waves are slotted behind it, otherwise new features wait on work that no longer protects the shipped product.
2. **Wave A ahead of the gate?** A1-A3 are correctness fixes to shipped behaviour, so they arguably should not wait.
3. **AI:** MCP design tools only (D2), or an in-app assistant as well.
4. **Ignite 2026 is 17-20 November.** Re-check Bicep, stacks and Copilot announcements before starting Waves B and C.

## 5. Research limits

Unverified or from search snippets only: most retirement dates, the stacks what-if api-version, FOCUS export GA status, Azure MCP Server 3.0 details, Holori and System Initiative (sites unreachable). The upstream repo is archived with no open issues, so there are no user requests to mine.

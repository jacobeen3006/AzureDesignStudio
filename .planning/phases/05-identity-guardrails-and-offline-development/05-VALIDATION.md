---
phase: "05"
slug: "identity-guardrails-and-offline-development"
status: draft
nyquist_compliant: false
wave_0_complete: false
created: "2026-11-14"
---

# Phase 05 - Validation Strategy

> Lifted from 05-RESEARCH.md `## Validation Architecture`; plans must map their tasks onto its rows.

- **Max feedback latency:** 120 seconds
- **Per task:** quick filter run; **per wave:** Core.Tests offline + Server.Tests (Release, -p:RunAOTCompilation=false); **phase gate:** both suites green, SQL Server container run (`bash src/RunSqlServerTests.sh`) for the migration.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit 2.9 (Server.Tests, Core.Tests) |
| Config file | none beyond csproj; Server.Tests has `AdsServerFactory` (env "Testing", Sqlite temp file) |
| Quick run command | `dotnet test src/AzureDesignStudio.Server.Tests --no-restore -p:RunAOTCompilation=false --filter "FullyQualifiedName~Identity\|FullyQualifiedName~Authorization\|FullyQualifiedName~CryptoProvider"` |
| Full suite command | `dotnet test src/AzureDesignStudio.Server.Tests -p:RunAOTCompilation=false` and `dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"`; SQL Server migration tests via `src/RunSqlServerTests.sh`; `node --test src/AzureDesignStudio/JsTests/*.test.cjs` |

### Phase Requirements -> Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| IDA-01 | table-driven pure tests: missing/non-GUID/empty oid or tid, both spellings, conflicting spellings, `sub`-only -> no identity | unit | `--filter AdsIdentityTests` | ❌ Wave 0 |
| IDA-01 | via real host, header-driven claims: every Design/Deploy RPC with bad claim returns in-band 401, never `RpcException`; `DesignGrpcService` with a fake offline store makes zero `SaveAsync` calls | integration | `--filter IdentityRpcTests` | ❌ Wave 0 |
| IDA-02 | token with only the URI claim reads/saves/deletes designs and links created with the same Guid; quota counts the same rows | integration | `--filter LegacyClaimTests` | ❌ Wave 0 |
| IDA-03 | enumerate `EndpointDataSource` gRPC endpoints; each protected or on `{ "/Cost/GetRetailPrices" }`; `FallbackPolicy` set; unauthenticated call to every protected method -> `Unauthenticated`; Development-environment host also covered (reflection service) | integration | `--filter AuthorizationSurfaceTests` | ❌ Wave 0 |
| IDA-04 | Get/Set guardrail: org caller without role -> 403; with role -> 200; MSA caller (tid = consumer) -> 200 on own scope; two MSA callers isolated | integration | `--filter GuardrailAuthorizationTests` | ❌ Wave 0 |
| IDA-05 | two tenants write distinct policies and approvals for the same plan hash; neither reads or overwrites the other; legacy `Id=1` row governs a tenant with no row; tenant row replaces it | integration + migration | `--filter TenantIsolationTests`; `DeployDbMigrationTests`; `SqlServerMigrationTests` (container) | ❌ Wave 0 / extend |
| IDA-06 | approve without `Deploy.Approver` -> 403 code A; requester==approver with setting off -> 403 code B (independent); setting on -> allowed; setting change writes an audit event in the same commit; Desktop `InProcessTests` still approves | integration | `--filter ApprovalRulesTests`, `InProcessTests` | ❌ Wave 0 / extend |
| CRED-01 | Development provider: SaveSubscriptionInfo 200, LoadSubscriptionInfo returns it, decrypt round trip, with no vault names and no Azure credential; key ring in a temp directory | integration | `--filter DevelopmentCryptoTests` | ❌ Wave 0 |
| CRED-02 | `Register` with Production + Development throws before registering; Production + KeyVault with no vault still boots and `SaveSubscriptionInfo` answers 503; unknown value still rejected | unit + integration | `--filter CryptoProviderTests` | ✅ extend (signature changes) |
| (support) | service worker excludes `appsettings*.json`; `appsettings.json` still present in the assets manifest | source guard | `node --test src/AzureDesignStudio/JsTests/*.test.cjs` or `SourceGuardTests` | ❌ Wave 0 |
| (support) | no `x-test-user`/`TestAuthHandler` in `src/AzureDesignStudio.Server`; no `#if` in Program.cs | source guard | `--filter SourceGuardTests` | ✅ exists |

### Sampling Rate
- **Per task commit:** the quick filter above.
- **Per wave merge:** full Server.Tests plus Core.Tests offline.
- **Phase gate:** full suites plus `src/RunSqlServerTests.sh` green before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] Extend `TestAuthHandler` with a repeated `x-test-claim: <type>|<value>` header and default `tid` (03-RESEARCH already recommended this shape)
- [ ] Extend `TestServerCallContext.ForUser`/`WithClaims` with default tid, roles, single-user marker, and a "no tid" variant
- [ ] `AdsIdentityTests`, `AuthorizationSurfaceTests`, `TenantIsolationTests`, `ApprovalRulesTests`, `DevelopmentCryptoTests`, `IdentityRpcTests`
- [ ] Update `SqlServerMigrationModelTests`/`SqlServerMigrationTests` table lists and the legacy fixture expectations; `DeployDbMigrationTests` seeds
- [ ] Framework install: none


## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending

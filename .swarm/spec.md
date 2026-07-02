# Specification: Azure Design Studio Platform Refresh

## Feature Description

Cloud architects use AzureDesignStudio to visually design Azure infrastructure architectures and export them as deployable ARM/Bicep templates. The tool must continue functioning against the current Azure cloud platform as Azure APIs, authentication protocols, and resource offerings evolve.

Users need the generated templates to deploy without errors, the resource catalog to accurately reflect what Azure currently offers, and authentication flows to work with current Microsoft Entra ID (formerly Azure AD) requirements. Without this refresh, previously working designs may fail to deploy, resource types may be missing or stale, and the application itself may not build or run on modern infrastructure.

## User Scenarios

### Scenario 1: Architect deploys existing saved design
**Given** a cloud architect has an existing Azure architecture saved in the tool
**When** they export and deploy the ARM/Bicep template to Azure
**Then** the deployment succeeds without API errors, deprecated resource type warnings, or authentication failures

### Scenario 2: Architect creates new design with current resources
**Given** a cloud architect opens the tool to design a new architecture
**When** they browse the resource catalog for available service types
**Then** the catalog reflects currently available Azure resource types, tiers, and configurations

### Scenario 3: Architect authenticates to deploy
**Given** a cloud architect wants to deploy a design from the tool
**When** they authenticate via the configured identity provider
**Then** the authentication flow completes successfully and the deployment operation proceeds

### Scenario 4: Developer builds and runs the application
**Given** a developer clones the repository
**When** they build and run the application
**Then** the build succeeds with no package conflicts, framework errors, or security warnings

## Functional Requirements

**FR-001** — The generated ARM templates MUST deploy successfully against the current Azure Resource Manager API surface without producing deprecated operation or API version warnings.

**FR-002** — The generated Bicep files MUST compile and deploy with the current Bicep CLI version, producing valid ARM templates.

**FR-003** — The authentication integration MUST work with current Microsoft Entra ID endpoints and token issuance requirements.

**FR-004** — The resource definition catalog MUST cover the same 14 Azure resource types at minimum, with resource properties and API versions updated to those currently supported by Azure.

**FR-005** — The application MUST build with no errors on a development machine running current .NET tooling.

**FR-006** — The application MUST run in a containerized environment using current base images without security vulnerabilities.

**FR-007** — Package dependencies MUST resolve without version conflicts, deprecation warnings, or known-vulnerability advisories.

**FR-008** — The gRPC communication layer between the client and server MUST remain compatible after all dependency updates.

**FR-009** — The Roslyn source generator MUST continue producing valid DTO code with the updated compiler SDK.

**FR-010** — The Azure Key Vault integration MUST successfully retrieve deployment credentials and certificates using current SDK APIs.

**FR-011** — The database layer (both in-memory and SQL Server) MUST initialize, migrate, and persist design state without errors after the platform update.

## Success Criteria

**SC-001** — All generated ARM template deployment operations succeed against current Azure Resource Manager (verified with at least one test deployment per resource category: Network, Compute, Data, Integration, Storage).

**SC-002** — The application builds from clean checkout with `dotnet build` producing exit code 0 and zero NU-package-related warnings.

**SC-003** — All existing unit tests pass (`dotnet test` exit code 0).

**SC-004** — Authentication flow completes end-to-end: login page renders, credentials are accepted, tokens are issued, and the authenticated session persists without unexpected redirects or errors.

**SC-005** — Docker image builds successfully and the application starts in the container without runtime errors.

**SC-006** — The Bicep decompiler produces valid output that compiles with `az bicep build`.

## Key Entities

- Azure Architecture Design (saved workspace with placed resources and connections)
- Azure Resource Definition (resource type catalog entries with properties and constraints)
- Deployment Template (exported ARM JSON or Bicep file)
- User Session (authenticated state backed by identity tokens)
- Design Persistence Store (saved and retrieved designs)

## Edge Cases and Known Failure Modes

- **API version drift**: A currently-used API version for a resource type may be deprecated in Azure. Deployments using that version produce warnings or failures. The resource catalog must track supported API versions across Azure SDK updates.
- **Authentication protocol changes**: Microsoft Entra ID endpoints or token formats may change. If the auth library is pinned too old, login may break silently.
- **Bicep language evolution**: Newer Bicep versions introduce syntax changes. The decompiler must generate Bicep compatible with the Bicep CLI version used at deployment time.
- **Source generator API incompatibility**: Roslin source generators depend on the compiler API surface, which can change between SDK versions. The source gen project must be validated separately.
- **Trimming breakage**: .NET publishing with trimming enabled may exclude assemblies that are loaded dynamically (e.g., AntDesign components, authentication handlers), causing runtime failures not caught at build time.
- **gRPC-Web protocol mismatch**: Both client and server gRPC packages must be updated in lockstep. Version mismatch in the wire protocol causes silent connection failures.
- **Docker base image age**: Base images from 2023 contain known OS-level CVEs. Running on outdated base images may violate organizational security policies.

## Assumptions (Resolved Clarifications)

- Target platform: .NET 8 LTS (not .NET 9 STS) — the most conservative upgrade path with longest support runway.
- Azure SDK packages will be updated to the latest stable (non-preview) versions available at the time of work.
- The Blazor.Diagrams submodule (custom fork) requires only TFm target change, not a full rewrite.
- All gRPC packages can be updated together to the same new version.
- The existing Azure AD B2C tenant configuration remains valid — only the client library integration needs updating.
- `Azure.Deployments.Core` is an unsupported package used only for type helpers; migration path is to remove it and use `Azure.ResourceManager` types exclusively.
- The Dockerfile will be updated to use .NET 8 base images matching the target framework.

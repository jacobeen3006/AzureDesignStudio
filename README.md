# Azure Design Studio

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)

**Azure Design Studio** is a desktop app for drawing Azure architectures visually and turning them into deployable infrastructure. Draw a design, check it against Azure's rules, estimate its cost, export it as ARM or Bicep, and run a What-If against a resource group before anything is deployed.

The app runs entirely on your machine. There is no server to host: it calls Azure directly with your own sign-in.

> **Status:** the desktop app is the primary target and is under active development, on Windows and on macOS (Mac Catalyst, installed from a `.dmg`). The browser (WebAssembly) version and its gRPC server still build and pass their tests, but they receive no new features.

![Hub-and-spoke design in Azure Design Studio](docs/screenshots/canvas-hub-spoke.png)

---

## Features

### Design
- **Drag-and-drop canvas** with resources grouped the way Azure nests them: virtual networks contain subnets, subnets contain resources, and so on.
- **Resource palette** grouped into Gallery, Networking, Compute, Containers, Databases, Storage, Integration, Security & Ops, and AI & Analytics.
- **Gallery designs** to start from: hub-and-spoke, an API Management landing zone, and an AKS baseline architecture.
- **Inspector panel** for each resource's properties, plus a command palette (`Ctrl+K`) for every action.
- **Light and dark themes.**

### Check
- **Validate the design** against Azure resource rules (names, address ranges, required subnets, and similar), before you export or deploy.
- **Suggest globally unique resource names** for storage accounts, Key Vaults, and other names that must be unique.
- **Estimate monthly cost** from Azure's public retail price list.

### Export
- **ARM template** (JSON), with a preview and a download button.
- **Bicep** source.
- **Image** of the diagram.
- **Text design** (`.ads.json`) for saving and sharing a design. You can import these back in.

### Deploy
- **What-If review** shows exactly which resources will be created, changed, or deleted in the target resource group before anything runs.
- **Approve & deploy** runs the reviewed plan, and only that plan. Changes are blocked when the What-If shows deletions or when a guardrail protects a resource.
- **Deployment stacks** as an option, so the resources of one design are tracked and managed as a single unit.
- **Linked service principals** per subscription, with the secret kept in the Windows Credential Manager or the macOS Keychain. Without one, the app uses your own signed-in identity.

### Analyze existing environments
- **Tenant scan** reads what is already deployed in your subscriptions and shows it alongside your designs.
- **Compare saved designs** to see what changed between two versions.
- **Guardrails** mark resources that the app must not touch.

---

## See it in action

### Design a landing zone
Drag a gallery design onto the canvas, or build one from the palette. Click a resource to inspect it, and use the action buttons to validate, estimate cost, export, or save.

![Hub-and-spoke design](docs/screenshots/canvas-hub-spoke.png)

![Selected resource in the inspector](docs/screenshots/design-inspector.png)

### Estimate the monthly cost
Choose a region and currency, then press **Calculate**. Each resource gets one line:

- **Computed**: priced from Azure's retail price list for the size you chose (for example a VM's SKU).
- **UsageBased**: a flat hourly or per-unit rate, shown with its unit.
- **Unavailable**: priced by usage (such as public IP traffic), so it is left out of the total.
- **Free**: networks and subnets carry no charge of their own.

The total and the breakdown by service update with every calculation.

![Monthly cost estimate](docs/screenshots/cost-estimate.png)

### Deploy safely
1. **Export → ARM template** shows the generated template. Download it, or press **Deploy**.
2. Choose the subscription and resource group. The app shows the parameters the template needs.
3. **What-If** shows what would be created, modified, or deleted, before anything runs.
4. **Approve & deploy** is enabled only when the What-If allows it. It is blocked if the plan deletes anything or touches a guardrailed resource, and it runs exactly the plan you reviewed.

![What-If review before deployment](docs/screenshots/whatif-review.png)

> Subscription IDs and resource names are redacted in these pictures.

---

## Install

### Windows

#### Requirements

| | |
|---|---|
| **Operating system** | Windows 10 version 2004 (build 19041) or later, x64 |
| **.NET SDK** | .NET 10 SDK (10.0.4xx or later) |
| **Workloads** | .NET MAUI (`dotnet workload install maui-windows`) |
| **Azure access** | An Azure account with at least Reader on the subscriptions you want to see. Deploying needs Contributor (or the role the deployment requires). |
| **Optional** | Azure CLI (`az`), used as a sign-in fallback when the Windows broker is unavailable |

#### Build and run

```bash
git clone https://github.com/jacobeen3006/AzureDesignStudio.git
cd AzureDesignStudio

dotnet workload install maui-windows

dotnet build src/AzureDesignStudio.Desktop -c Debug -f net10.0-windows10.0.19041.0
```

Then start the built app:

```bash
src/AzureDesignStudio.Desktop/bin/Debug/net10.0-windows10.0.19041.0/win-x64/AzureDesignStudio.Desktop.exe
```

Or open `src/AzureDesignStudio.sln` in Visual Studio 2022 with the .NET MAUI workload installed, select `AzureDesignStudio.Desktop` as the startup project, and press F5.

### macOS

The macOS app is the same desktop app built for Mac Catalyst: one universal build for Apple silicon and Intel Macs, macOS 14 (Sonoma) or later.

#### Install from the disk image

1. Download `AzureDesignStudio-<version>-macos-universal.dmg` from the GitHub release (or build it, below), open it and drag **Azure Design Studio** onto **Applications**.
2. A build that is not signed with a Developer ID (the default, see below) is blocked by Gatekeeper on first launch. Right-click the app and choose **Open**, or clear the quarantine flag once:

   ```bash
   xattr -dr com.apple.quarantine "/Applications/Azure Design Studio.app"
   ```

#### Build the disk image

| | |
|---|---|
| **Operating system** | macOS 14 or later, with Xcode 27 |
| **.NET SDK** | .NET 10 SDK (10.0.4xx or later) |
| **Workloads** | `dotnet workload install maui-maccatalyst` (run it outside the repository folder, or `dotnet workload restore` inside it, so it matches `global.json`) |

```bash
src/BuildMacDmg.sh
```

The image lands in `artifacts/`. Options: `--version 1.2.3`, `--arch arm64|x64` (default `universal`), `--sign "Developer ID Application: …"` to sign with your certificate and the hardened runtime, and `--notarize <keychain-profile>` to notarize and staple (`xcrun notarytool store-credentials` creates the profile). Without `--sign` the app is ad-hoc signed.

Tagged releases (`v*`) build the same image in the `desktop-dmg` job of [`publish.yml`](.github/workflows/publish.yml) and attach it to the GitHub release. It is ad-hoc signed unless these repository secrets are set: `MACOS_CERT_P12_BASE64`, `MACOS_CERT_PASSWORD` and `MACOS_SIGN_IDENTITY` (a Developer ID Application certificate) to sign, plus `APPLE_ID`, `APPLE_TEAM_ID` and `APPLE_APP_PASSWORD` (an app-specific password) to notarize. The job needs a runner image with Xcode 27 (`macos-26` by default; the `MACOS_RUNNER` repository variable picks another).

For development, build and run the app directly:

```bash
dotnet build src/AzureDesignStudio.Desktop -f net10.0-maccatalyst
open "src/AzureDesignStudio.Desktop/bin/Debug/net10.0-maccatalyst/maccatalyst-arm64/Azure Design Studio.app"
```

### Sign in

1. Open **User account** in the top menu and choose **Sign in**.
2. Pick your account in the Windows sign-in window, or on macOS in the browser window that opens. The app asks Azure for permission to manage resources on your behalf (`user_impersonation`).

The sign-in uses the Entra app registration whose client ID is set in [`src/AzureDesignStudio.Desktop/appsettings.json`](src/AzureDesignStudio.Desktop/appsettings.json) (`AzureAd:ClientId`). The registration is a public client that accepts both work and personal Microsoft accounts. To use your own registration, create one with:

- supported account types: accounts in any organizational directory and personal Microsoft accounts
- *Allow public client flows*: enabled
- redirect URIs: `ms-appx-web://microsoft.aad.brokerplugin/<client-id>` and `http://localhost`
- API permission: Azure Service Management → `user_impersonation` (delegated)

Then put its client ID in `appsettings.json`.

macOS has no sign-in broker: the browser sign-in returns to the app on the `http://localhost` redirect, and the app keeps the account in the login Keychain so it stays signed in across launches.

If the Windows sign-in broker is not available, or you have not signed in, the app falls back to the token from `az login` (on macOS it looks for `az` in `/opt/homebrew/bin` and `/usr/local/bin` too). Signing out turns that fallback off.

### Where your data lives

Everything is stored locally in the app's data folder (on Windows MAUI's `FileSystem.AppDataDirectory`; on macOS `~/Library/Application Support/com.azuredesignstudio.desktop`):

- `workspace.db`: saved designs, scans, and snapshots
- `deploy.db`: deployment approvals, guardrails, linked subscriptions, and the audit log

Saved designs stay on your machine and are not uploaded anywhere.

---

## Screenshots

**ARM template export.** Preview the generated template, then download it or deploy it.

![ARM template export preview](docs/screenshots/arm-export.png)

---

## Solution structure

```
AzureDesignStudio.sln
├── AzureDesignStudio.Desktop          — .NET MAUI Blazor Hybrid app for Windows and macOS (the main target)
├── AzureDesignStudio                  — Blazor WebAssembly client (browser build, no new features)
├── AzureDesignStudio.Services         — Service logic for designs, deployment, and prices; runs in-process in the desktop app
├── AzureDesignStudio.Server           — ASP.NET Core gRPC host for the browser build
├── AzureDesignStudio.Core             — Resource models, diagram logic, ARM generation, validation
├── AzureDesignStudio.AzureResources   — ARM resource type definitions
├── AzureDesignStudio.Discovery        — Tenant scans, What-If, guardrails, deployment stacks
├── AzureDesignStudio.Workspace        — SQLite storage for designs, scans, and audit
├── AzureDesignStudio.SharedModels     — Protobuf contracts
├── AzureDesignStudio.Mcp              — Read-only MCP server over the local workspace
├── AzureDesignStudio.SourceGeneration — Roslyn source generator for DTO mappings
├── AzureDesignStudio.Core.Tests       — Unit tests (run with `--filter "Category!=LiveAzure"`)
└── AzureDesignStudio.Server.Tests     — Tests of the gRPC services and the in-process host
```

### Tests

```bash
dotnet test src/AzureDesignStudio.Core.Tests --filter "Category!=LiveAzure"
dotnet test src/AzureDesignStudio.Server.Tests
```

---

## Built with

- .NET 10, Blazor, .NET MAUI Blazor Hybrid
- [Ant Design Blazor](https://antblazor.com/) for the UI
- [Z.Blazor.Diagrams](https://github.com/Blazor-Diagrams/Blazor.Diagrams) for the canvas
- Microsoft Authentication Library (MSAL) with the Windows account broker, or the system browser on macOS
- Entity Framework Core with SQLite
- Azure.ResourceManager and Azure.Deployments.Core

---

## Background

Azure Design Studio won **3rd place** in the Microsoft Global Hackathon 2022.

## License

Released under the [GNU General Public License v3.0](https://www.gnu.org/licenses/gpl-3.0).

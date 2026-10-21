# Entra App Registration & Desktop Build Guide

Azure Design Studio signs users in with **Microsoft Entra ID** (work/personal
accounts; B2C is no longer used). Three pieces consume the registration:

| Piece | Client id used | Audience of the token |
|---|---|---|
| WASM browser app | `AzureAd:ClientId` in `src/AzureDesignStudio/wwwroot/appsettings.json` | server API scope |
| MAUI desktop app | `AzureAd:ClientId` in Desktop `appsettings.json` (beside the exe) | server API scope |
| Server (validation) | `ServerScope` = `api://<server-app-id>/Server.Access` | its own JWT keys |

## 1. Register the server API app

1. Entra admin center → **App registrations** → *New registration* (single tenant
   or multi-tenant, as your org requires). Note the **Application (client) ID** — this is the *server app id*.
2. **Expose an API**:
   - Application ID URI: `api://<server-app-id>`
   - Add a scope: `Server.Access` — *Admins and users*, "Access the ADS server".
3. **Authentication** (for WASM): add platform *Single Page Application*,
   redirect URI `https://<your-host>/authentication/login` (plus
   `http://localhost:5203/authentication/login` for local runs).
4. Token configuration: optional claims as needed; the server validates the
   `scp` claim against `Server.Access` (`RequiredScope` in `Program.cs`).

## 2. Register the client app(s)

You can reuse the server app for clients or register a separate SPA/native app:

- **WASM**: platform *Single Page Application*; add the same redirect URIs;
  under *API permissions*, add the `Server.Access` scope from step 1.
- **Desktop**: platform *Mobile & desktop* (public client — no secret).
  MSAL uses the **Windows broker (WAM)** automatically; the **device code
  flow** is the fallback, so no redirect URI is required. Add the
  `Server.Access` API permission.

## 3. Configure the app

`src/AzureDesignStudio/wwwroot/appsettings.json` (WASM):

```json
{ "AzureAd": { "Authority": "https://login.microsoftonline.com/common",
               "ClientId": "<client app id>",
               "ValidateAuthority": true },
  "ServerScope": "api://<server-app-id>/Server.Access" }
```

Desktop `appsettings.json` (copied next to the exe, same shape) adds
`ServerUrl` — the native gRPC endpoint, e.g. `https://ads.example.com:5001` or
`http://localhost:5203` for development.

The repository ships **placeholders** (`<client id>`,
`api://<server client id>/Server.Access`). With placeholders the apps still
work anonymously: sign-in fails gracefully and designs can be exported as
ARM/Bicep files; the desktop app additionally keeps designs in its offline
cache. Save/load/deploy against the server requires the real ids.

Server-side (`appsettings`/Key Vault in production):

- `AzureAd:Instance/TenantId/ClientId/Audience` matching the server app,
- `Authority` `https://login.microsoftonline.com/common` (or the tenant),
- the JWT signing key source (`ServerScopeKey` / DataProtection keys as configured in `Program.cs`).

## 4. Build & package the desktop app

Prereq: .NET 10 SDK with `dotnet workload install maui-windows`.

```powershell
# Run unpackaged (development default, WindowsPackageType=None)
dotnet build src/AzureDesignStudio.Desktop
src\AzureDesignStudio.Desktop\bin\Debug\net10.0-windows10.0.19041.0\win-x64\AzureDesignStudio.Desktop.exe

# Build an MSIX installer
dotnet publish src/AzureDesignStudio.Desktop -c Release -f net10.0-windows10.0.19041.0 `
  -p:WindowsPackageType=MSIX -p:RuntimeIdentifier=win-x64 -p:WindowsAppSDKSelfContained=true
```

The MSIX lands under `bin\Release\...\Publish\win-x64\` (`.msix` / `.appx`,
plus an install PowerShell script). The manifest
(`Platforms/Windows/Package.appxmanifest`) uses
`Publisher="CN=Azure Design Studio"`, so a locally built package must be
**signed** (and the cert trusted) before Windows will install it — see
`.github/workflows/publish.yml` (`MSIX_PFX_BASE64` / `MSIX_PFX_PASSWORD`
secrets sign automatically; without them the artifact is uploaded unsigned).
For Store distribution change Identity/Publisher to your Store identity via
Partner Center packaging.

The publisher identity is decided: Azure Artifact Signing (formerly Trusted
Signing) with individual validation, certificate subject `CN=Jakub Talaj`
(Phase 1, 2026-10-21). The manifest's future `Publisher` must equal that
subject verbatim; `PublisherDisplayName` may remain "Azure Design Studio".
The manifest still carries the placeholder `Publisher="CN=Azure Design
Studio"` and is not edited in Phase 1. Phase 11 makes the manifest
`Publisher` equal the certificate subject and asserts it in CI, because a
mismatch fails installation with `0x8007000B`.

## 5. Offline behaviour (desktop)

- Designs saved while the server is unreachable are written under
  `%LocalAppData%\AzureDesignStudio…signs\*.adsdesign.json`.
- They are listed alongside server designs and always win over the server
  copy until they sync (next successful save/list/load pushes them).
- Deleting a design removes both copies.

---
phase: 02-reproducible-build-and-ci-baseline
created: 2026-10-21
status: awaiting-runs
---

# Desktop (windows-latest) runs

## Local baseline

| Build | Seconds | Notes |
|---|---|---|
| Clean Desktop Release build, AOT disabled | 74 | local — not a CI run; command: `dotnet build src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj -c Release --no-restore -p:RunAOTCompilation=false` |
| Clean Desktop Release build, AOT enabled | 466 | local — not a CI run; command: `dotnet build src/AzureDesignStudio.Desktop/AzureDesignStudio.Desktop.csproj -c Release --no-restore` |

## Runs

| Run | Date (UTC) | Commit | Job | continue-on-error | Conclusion | Seconds | Source | URL |
|---|---|---|---|---|---|---|---|---|

## SC#2 — deliberate compile error

- Branch:
- PR link (closed, unmerged):
- Run URL:
- Failing job:
- CS error naming `MauiProgram.cs`:
- Guard:

## SC#5

Flip commit:
Required check: Desktop (windows-latest)
Proof:

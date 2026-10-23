#!/usr/bin/env pwsh

Write-Host "Get Nerdbank Git Version."

nbgv get-version -f json | Out-File .\nbgv-version.json

.\GenerateVersioningTargets.ps1

# The Dockerfile needs the repository-root build inputs (global.json,
# Directory.Build.props, Directory.Packages.props, Directory.Build.targets), so the
# build context is the repository root even though this script runs from src/.
$repoRoot = (Resolve-Path ..).Path
docker build -f ./AzureDesignStudio.Server/Dockerfile -t azuredesignstudioserver --force-rm $repoRoot

Remove-Item -Path .\nbgv-version.json, .\Directory.Build.targets

Write-Host "Build completed."

#!/usr/bin/env pwsh

# Builds the server image. The Docker context is the repository root (the
# central build files live there); .git is excluded from it, so the version is
# computed here with nbgv and handed to the build through a generated
# src/Directory.Build.targets.

param(
    [string] $Tag = 'azuredesignstudioserver'
)

$ErrorActionPreference = 'Stop'

Push-Location $PSScriptRoot
try {
    Write-Host "Get Nerdbank Git Version."

    nbgv get-version -f json | Out-File .\nbgv-version.json
    if ($LASTEXITCODE -ne 0) { throw "nbgv get-version failed with exit code $LASTEXITCODE." }

    .\GenerateVersioningTargets.ps1
    if ($LASTEXITCODE -ne 0) { throw "GenerateVersioningTargets.ps1 failed with exit code $LASTEXITCODE." }

    docker build -f ./AzureDesignStudio.Server/Dockerfile -t $Tag --force-rm ..
    if ($LASTEXITCODE -ne 0) { throw "docker build failed with exit code $LASTEXITCODE." }

    Write-Host "Build completed."
}
finally {
    Remove-Item -Path .\nbgv-version.json, .\Directory.Build.targets -ErrorAction SilentlyContinue
    Pop-Location
}

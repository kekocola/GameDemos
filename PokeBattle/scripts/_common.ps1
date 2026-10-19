#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Write-PokeStep {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Invoke-PokeNative {
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Command
    )

    Write-PokeStep $Description
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Assert-PokeCommand {
    param([Parameter(Mandatory)][string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found in PATH."
    }
}

function Assert-PokeEnvironment {
    Assert-PokeCommand "dotnet"
    Assert-PokeCommand "node"
    Assert-PokeCommand "npm"

    $sdkLines = @(& dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0 -or -not ($sdkLines | Where-Object { $_ -match '^10\.' })) {
        throw ".NET SDK 10.x is required. Installed SDKs: $($sdkLines -join ', ')"
    }

    $nodeText = (& node --version).Trim().TrimStart('v')
    if ($LASTEXITCODE -ne 0) { throw "Unable to read the Node.js version." }
    $nodeVersion = [version]($nodeText.Split('-')[0])
    $nodeSupported =
        ($nodeVersion.Major -eq 20 -and $nodeVersion -ge [version]'20.19.0') -or
        ($nodeVersion.Major -eq 22 -and $nodeVersion -ge [version]'22.12.0') -or
        ($nodeVersion.Major -gt 22)
    if (-not $nodeSupported) {
        throw "Node.js 20.19+, 22.12+, or a newer release is required; found v$nodeText."
    }

    [pscustomobject]@{
        Repository = $script:RepositoryRoot
        DotNetSdk  = (& dotnet --version).Trim()
        Node       = "v$nodeText"
        Npm        = (& npm --version).Trim()
    }
}

function Assert-PokeClientDependencies {
    $nodeModules = Join-Path $script:RepositoryRoot "web\node_modules"
    if (-not (Test-Path -LiteralPath $nodeModules -PathType Container)) {
        throw "Frontend dependencies are missing. Run scripts\restore.ps1 first."
    }
}

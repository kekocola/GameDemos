#requires -Version 5.1

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidatePattern('^https?://')]
    [string]$Url = "http://localhost:5080",
    [switch]$NoBuild
)

. (Join-Path $PSScriptRoot "_common.ps1")

Assert-PokeEnvironment | Out-Null
Push-Location $script:RepositoryRoot
try {
    if (-not $NoBuild) {
        & (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration -SkipTests
    }

    $serverDll = Join-Path $script:RepositoryRoot "src\PokeBattle.Server\bin\$Configuration\net10.0\PokeBattle.Server.dll"
    if (-not (Test-Path -LiteralPath $serverDll -PathType Leaf)) {
        throw "Server build output was not found at '$serverDll'. Run scripts\build.ps1 first or omit -NoBuild."
    }

    Write-PokeStep "Start PokeBattle server"
    Write-Host "Battle lab: $Url"
    Write-Host "Press Ctrl+C to stop."
    Set-Location (Join-Path $script:RepositoryRoot "src\PokeBattle.Server")
    & dotnet $serverDll --urls $Url
    if ($LASTEXITCODE -ne 0) { throw "Server exited with code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

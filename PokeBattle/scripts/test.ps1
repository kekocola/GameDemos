#requires -Version 5.1

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipRestore,
    [switch]$SkipClient
)

. (Join-Path $PSScriptRoot "_common.ps1")

Assert-PokeEnvironment | Out-Null
Push-Location $script:RepositoryRoot
try {
    if (-not $SkipRestore) {
        & (Join-Path $PSScriptRoot "restore.ps1")
    }
    elseif (-not $SkipClient) {
        Assert-PokeClientDependencies
    }

    Invoke-PokeNative "Validate and refresh runtime configuration" {
        dotnet run --project "tools\PokeBattle.ConfigCompiler" --configuration $Configuration --no-restore -- "data\csv" "data\runtime\battle-config.json"
    }

    if (-not $SkipClient) {
        Invoke-PokeNative "Type-check and build H5 client" {
            npm run build --prefix "web"
        }
    }

    Invoke-PokeNative "Run .NET tests" {
        dotnet test "tests\PokeBattle.Tests\PokeBattle.Tests.csproj" --configuration $Configuration --no-restore --verbosity minimal -m:1 /nodeReuse:false
    }
}
finally {
    Pop-Location
}

Write-PokeStep "All validations passed"

#requires -Version 5.1

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipRestore,
    [switch]$SkipClient,
    [switch]$SkipTests
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

    Invoke-PokeNative "Compile and validate battle configuration" {
        dotnet run --project "tools\PokeBattle.ConfigCompiler" --configuration $Configuration --no-restore -- "data\csv" "data\runtime\battle-config.json"
    }

    if (-not $SkipClient) {
        Invoke-PokeNative "Build TypeScript H5 client" {
            npm run build --prefix "web"
        }
    }

    Invoke-PokeNative "Build .NET solution ($Configuration)" {
        dotnet build "PokeBattle.slnx" --configuration $Configuration --no-restore -m:1 /nodeReuse:false
    }

    if (-not $SkipTests) {
        Invoke-PokeNative "Run automated tests" {
            dotnet test "tests\PokeBattle.Tests\PokeBattle.Tests.csproj" --configuration $Configuration --no-build --no-restore --verbosity minimal -m:1 /nodeReuse:false
        }
    }
}
finally {
    Pop-Location
}

Write-PokeStep "Build completed successfully"
Write-Host "Configuration: $Configuration"
Write-Host "Server output: src\PokeBattle.Server\bin\$Configuration\net10.0"

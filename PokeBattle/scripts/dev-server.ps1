#requires -Version 5.1

param(
    [ValidatePattern('^https?://')]
    [string]$Url = "http://localhost:5080",
    [switch]$SkipRestore
)

. (Join-Path $PSScriptRoot "_common.ps1")

Assert-PokeEnvironment | Out-Null
Push-Location $script:RepositoryRoot
try {
    if (-not $SkipRestore) {
        Invoke-PokeNative "Restore .NET dependencies" {
            dotnet restore "PokeBattle.slnx" -m:1 /nodeReuse:false
        }
    }

    Invoke-PokeNative "Compile and validate battle configuration" {
        dotnet run --project "tools\PokeBattle.ConfigCompiler" --configuration Debug --no-restore -- "data\csv" "data\runtime\battle-config.json"
    }

    Write-PokeStep "Start ASP.NET Core watch server"
    Write-Host "Battle API: $Url"
    Write-Host "Press Ctrl+C to stop."
    dotnet watch --project "src\PokeBattle.Server\PokeBattle.Server.csproj" run --no-restore -- --urls $Url
    if ($LASTEXITCODE -ne 0) { throw "Development server exited with code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

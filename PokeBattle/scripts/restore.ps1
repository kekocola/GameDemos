#requires -Version 5.1

. (Join-Path $PSScriptRoot "_common.ps1")

Assert-PokeEnvironment | Out-Null
Push-Location $script:RepositoryRoot
try {
    Invoke-PokeNative "Restore .NET dependencies" {
        dotnet restore "PokeBattle.slnx" -m:1 /nodeReuse:false
    }

    Invoke-PokeNative "Install locked frontend dependencies" {
        npm ci --prefix "web"
    }
}
finally {
    Pop-Location
}

Write-PokeStep "Restore completed"

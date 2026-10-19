#requires -Version 5.1

param([switch]$SkipInstall)

. (Join-Path $PSScriptRoot "_common.ps1")

Assert-PokeEnvironment | Out-Null
Push-Location $script:RepositoryRoot
try {
    if (-not $SkipInstall) {
        Invoke-PokeNative "Install locked frontend dependencies" {
            npm ci --prefix "web"
        }
    }
    else {
        Assert-PokeClientDependencies
    }

    $clientUrl = "http://127.0.0.1:5173"
    Write-PokeStep "Start Vite development client"
    Write-Host "H5 client: $clientUrl"
    Write-Host "Requires dev-server.ps1 at http://localhost:5080."
    Write-Host "Press Ctrl+C to stop."
    npm run dev --prefix "web" -- --host 127.0.0.1 --port 5173 --open $clientUrl
    if ($LASTEXITCODE -ne 0) { throw "Vite exited with code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

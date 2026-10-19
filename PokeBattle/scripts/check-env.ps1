#requires -Version 5.1

. (Join-Path $PSScriptRoot "_common.ps1")

$environment = Assert-PokeEnvironment
Write-PokeStep "Environment is ready"
$environment | Format-List

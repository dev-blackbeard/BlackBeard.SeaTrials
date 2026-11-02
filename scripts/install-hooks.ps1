# Enable this repository's git hooks (the pre-push sanity check) for this clone.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
git config core.hooksPath scripts/hooks
if ($LASTEXITCODE -ne 0) { throw 'git config failed' }
Write-Host 'git hooks enabled: core.hooksPath = scripts/hooks'

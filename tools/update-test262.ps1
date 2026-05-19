param(
    [string]$RepoUrl = "https://github.com/tc39/test262.git",
    [string]$TargetDir = "external/test262",
    [string]$PinnedCommit = "main"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $TargetDir)) {
    git clone $RepoUrl $TargetDir
}

Push-Location $TargetDir
try {
    git fetch --all --tags --prune
    git checkout $PinnedCommit
    $resolved = (git rev-parse HEAD).Trim()
}
finally {
    Pop-Location
}

$pinPath = "external/test262.pin"
Set-Content -Path $pinPath -Value $resolved
Write-Host "Pinned test262 at $resolved"

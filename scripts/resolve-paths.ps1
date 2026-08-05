# Shared path-resolution helpers for FenBrowser scripts.
# Sourced by other .ps1 scripts: . "$PSScriptRoot\resolve-paths.ps1"
#
# Rules:
#   - Repo root is the parent of this scripts/ directory.
#   - test262/wpt checkouts resolve in this order:
#       1. explicit env var (TEST262_ROOT / WPT_ROOT)
#       2. sibling directory next to the repo
#       3. directory inside the repo
#   - Scripts should pass an explicit value when they need one; this helper
#     only supplies defaults so no machine-specific path survives in scripts.

function Resolve-FenRepoRoot {
    $here = Split-Path -Parent $PSScriptRoot
    if (Test-Path (Join-Path $here 'FenBrowser.sln')) {
        return $here
    }
    throw "FenBrowser repo root not found (expected FenBrowser.sln next to scripts/)."
}

function Resolve-Test262Root {
    param([string]$ExplicitRoot)
    if (-not [string]::IsNullOrWhiteSpace($ExplicitRoot)) {
        return $ExplicitRoot
    }
    if (-not [string]::IsNullOrWhiteSpace($env:TEST262_ROOT)) {
        return $env:TEST262_ROOT
    }

    $repo = Resolve-FenRepoRoot
    $sibling = Join-Path (Split-Path -Parent $repo) 'test262'
    if (Test-Path $sibling) {
        return $sibling
    }

    $local = Join-Path $repo 'test262'
    if (Test-Path $local) {
        return $local
    }

    throw "test262 checkout not found. Set TEST262_ROOT or place it as a sibling of the repo."
}

function Resolve-WptRoot {
    param([string]$ExplicitRoot)
    if (-not [string]::IsNullOrWhiteSpace($ExplicitRoot)) {
        return $ExplicitRoot
    }
    if (-not [string]::IsNullOrWhiteSpace($env:WPT_ROOT)) {
        return $env:WPT_ROOT
    }

    $repo = Resolve-FenRepoRoot
    $sibling = Join-Path (Split-Path -Parent $repo) 'wpt'
    if (Test-Path $sibling) {
        return $sibling
    }

    $local = Join-Path $repo 'wpt'
    if (Test-Path $local) {
        return $local
    }

    throw "WPT checkout not found. Set WPT_ROOT or place it as a sibling of the repo."
}

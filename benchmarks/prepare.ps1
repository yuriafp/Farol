<#
.SYNOPSIS
    Checks out a benchmark corpus at its pinned commit and prepares it the way its own build does (restore, generators),
    as benchmarks/corpora/<name>.json describes. Windows PowerShell 5.1 or PowerShell 7.

.EXAMPLE
    ./benchmarks/prepare.ps1 -Corpus umbraco
    ./benchmarks/prepare.ps1 -Corpus dnn-platform -Destination D:\corpora\dnn
#>
param(
    [Parameter(Mandatory = $true)] [string] $Corpus,
    [string] $Destination = (Join-Path $env:LOCALAPPDATA "Farol\evals\repos\$Corpus")
)

$ErrorActionPreference = 'Stop'

# The corpus files carry // comment lines, which ConvertFrom-Json does not accept everywhere.
$json = (Get-Content (Join-Path $PSScriptRoot "corpora\$Corpus.json") | Where-Object { $_ -notmatch '^\s*//' }) -join "`n"
$definition = $json | ConvertFrom-Json

if (-not (Test-Path (Join-Path $Destination '.git'))) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    git -C $Destination init -q
    git -C $Destination config core.longpaths true
    git -C $Destination remote add origin $definition.repository
}

$head = (git -C $Destination rev-parse --verify -q HEAD)
if ($head -ne $definition.commit) {
    Write-Host "Fetching $($definition.name) at $($definition.commit)..."
    git -C $Destination fetch -q --depth 1 origin $definition.commit
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed for $($definition.repository)" }
    git -C $Destination -c advice.detachedHead=false checkout -q --force FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw "git checkout failed" }
}

# {msbuild}: Visual Studio's MSBuild, for solutions whose projects import Visual Studio's targets.
$msbuild = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}

# Each step is an executable and its arguments, separated by spaces (no argument has one).
foreach ($step in $definition.prepare) {
    $parts = $step -split '\s+'
    $executable = $parts[0]
    if ($executable -eq '{msbuild}') {
        if (-not $msbuild) { throw "$Corpus needs Visual Studio's MSBuild (vswhere found none)." }
        $executable = $msbuild
    }

    $arguments = @($parts | Select-Object -Skip 1)
    Write-Host "> $step"
    Push-Location $Destination
    try {
        & $executable @arguments
        if ($LASTEXITCODE -ne 0) { throw "Preparation step failed ($LASTEXITCODE): $step" }
    }
    finally {
        Pop-Location
    }
}

Write-Host "$($definition.name) is ready in $Destination"

# Compiler oracle for eval tasks: marks declarations [Obsolete("FAROL-ORACLE:<tag>")], builds the solution with those
# warnings kept as warnings, and lists every place the compiler reports a use of each tagged declaration (C# CS0618, VB
# BC40000). A reference answer built this way comes from the compiler, not from Farol. The marked files are restored.
#
#   ./evals/oracle.ps1 -Repository <checkout> -Build '{msbuild} DNN_Platform.sln -t:Build -p:Configuration=Debug' `
#       -Marks @{ tag = 'gettab3'; file = 'DNN Platform/Library/Entities/Tabs/TabController.cs'; find = 'public TabInfo GetTab(int tabId, int portalId, bool ignoreCache)' }
#
# A mark's "find" text must occur exactly once in its file; the attribute goes right before it, so it must start a
# declaration, or an accessor ('set;' marks only the writes of a property).
param(
    [Parameter(Mandatory)] [string]$Repository,
    [Parameter(Mandatory)] [string]$Build,
    [Parameter(Mandatory)] [hashtable[]]$Marks,
    [string]$Log = (Join-Path ([IO.Path]::GetTempPath()) 'farol-oracle.log'))

$ErrorActionPreference = 'Stop'
$Repository = [IO.Path]::GetFullPath($Repository).TrimEnd('\', '/')
$files = @($Marks | ForEach-Object { $_.file } | Sort-Object -Unique)
if (git -C $Repository status --porcelain -- $files) { throw "Uncommitted changes in $($files -join ', '); the oracle restores these files with git." }

if ($Build.Contains('{msbuild}')) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msbuild = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    $Build = $Build.Replace('{msbuild}', "`"$msbuild`"")
}

$script = Join-Path ([IO.Path]::GetTempPath()) 'farol-oracle-build.cmd'
try {
    foreach ($mark in $Marks) {
        $path = Join-Path $Repository $mark.file
        $bytes = [IO.File]::ReadAllBytes($path)
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $text = [IO.File]::ReadAllText($path)
        $at = $text.IndexOf($mark.find, [StringComparison]::Ordinal)
        if ($at -lt 0 -or $text.IndexOf($mark.find, $at + 1, [StringComparison]::Ordinal) -ge 0) { throw "'$($mark.find)' must occur exactly once in $($mark.file)." }
        $attribute = if ($path.EndsWith('.vb')) { "<System.Obsolete(""FAROL-ORACLE:$($mark.tag)"")> " } else { "[System.Obsolete(""FAROL-ORACLE:$($mark.tag)"")] " }
        [IO.File]::WriteAllText($path, $text.Insert($at, $attribute), [Text.UTF8Encoding]::new($bom))
    }

    # Obsolete warnings must stay warnings, or the first failing project would hide its dependents' uses.
    Set-Content -Path $script -Encoding ascii -Value "@$Build -p:NuGetAudit=false -p:TreatWarningsAsErrors=false -p:WarningsNotAsErrors=CS0618%%3BBC40000 -m -nologo -v:q -fl ""-flp:logfile=$Log;verbosity=quiet;encoding=utf-8"""
    Push-Location $Repository
    $output = & cmd.exe /c $script 2>&1
    Pop-Location
    if (-not (Test-Path $Log)) { $output | Select-Object -Last 20; throw "The build wrote no log." }
}
finally {
    git -C $Repository checkout -q -- $files
    Remove-Item $script -ErrorAction SilentlyContinue
}

$uses = Select-String -Path $Log -Pattern 'FAROL-ORACLE:([\w-]+)' | ForEach-Object {
    $tag = $_.Matches[0].Groups[1].Value
    if ($_.Line -match '^\s*(?<file>.+?\.(cs|vb))\((?<line>\d+),\d+\)') {
        $full = [IO.Path]::GetFullPath($Matches.file)
        $relative = if ($full.StartsWith($Repository, [StringComparison]::OrdinalIgnoreCase)) { $full.Substring($Repository.Length + 1) } else { $full }
        [pscustomobject]@{ Tag = $tag; File = $relative.Replace('\', '/'); Line = [int]$Matches.line }
    }
} | Sort-Object Tag, File, Line -Unique
foreach ($group in $uses | Group-Object Tag) {
    "== $($group.Name): $($group.Count) use(s) in $(@($group.Group | Select-Object -ExpandProperty File -Unique).Count) file(s)"
    $group.Group | ForEach-Object { "$($_.File):$($_.Line)" }
}
if (-not $uses) { 'No tagged uses found; see ' + $Log }

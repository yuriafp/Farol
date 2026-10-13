<#
.SYNOPSIS
Runs the Visual Studio smoke checks (docs/smoke-tests.md, V1-V6) on the server side, without a client.

.DESCRIPTION
Restores a fresh copy of tests/fixtures/legacy, writes next to Legacy.sln the .mcp.json that Visual Studio reads, starts
the server from it as Visual Studio does, in the developer environment of the newest Visual Studio, and makes the calls
an agent makes in V1-V6, checking each answer. What only the client shows (the agent finding the server, the tools
picker, the prompts list in the chat box, the permission prompt) is not covered: AC-36 of spec 001 says when a person
also runs V1-V6 in Visual Studio. The usage log stays off.

.PARAMETER Version
The Farol.Mcp version to start.

.PARAMETER Packages
The folder that holds the release candidate's package. Default: artifacts/packages. Ignored with -Published.

.PARAMETER Published
Start the version published on nuget.org instead of a local candidate.

.PARAMETER Keep
Keep the fixture copy and the throwaway NuGet cache. Both are deleted when every check passes.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tests/smoke/visual-studio.ps1 -Version 0.1.0-alpha.5
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Packages,
    [switch]$Published,
    [switch]$Keep)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Packages) { $Packages = Join-Path $repo 'artifacts\packages' }

# The newest Visual Studio with MSBuild, previews included.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vs) { throw 'No Visual Studio with MSBuild was found.' }

# A restored copy of the legacy fixture outside the repository, so that it is the server's root.
$work = Join-Path ([IO.Path]::GetTempPath()) "farol-vs-checks-$(Get-Random)"
$cache = "$work-nuget"
Copy-Item (Join-Path $repo 'tests\fixtures\legacy') $work -Recurse
Get-ChildItem -LiteralPath $work -Recurse -Directory -Force | Where-Object { $_.Name -in 'bin', 'obj' } | Remove-Item -Recurse -Force
& (Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe') (Join-Path $work 'Legacy.sln') -t:Restore -p:RestorePackagesConfig=true -nodeReuse:false -v:q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Restoring the fixture failed (MSBuild exit code $LASTEXITCODE)." }

# The .mcp.json form Visual Studio reads, with a throwaway NuGet cache: a candidate in the real cache would later
# shadow the published package of the same version.
$serverArgs = @('dnx', "Farol.Mcp@$Version", '--yes')
if (-not $Published) { $serverArgs += @('--add-source', ($Packages -replace '\\', '/')) }
$mcp = [ordered]@{ servers = [ordered]@{ farol = [ordered]@{
    type = 'stdio'; command = 'dotnet'; args = $serverArgs; env = [ordered]@{ NUGET_PACKAGES = $cache } } } }
[IO.File]::WriteAllText((Join-Path $work '.mcp.json'), ($mcp | ConvertTo-Json -Depth 6))

$surface = Get-Content (Join-Path $repo 'tests\Farol.Tools.Tests\Snapshots\mcp-surface.json') -Raw | ConvertFrom-Json
$results = [Collections.Generic.List[string]]::new()
$script:failed = $false
function Check([string]$id, [bool]$pass, [string]$detail) {
    if (-not $pass) { $script:failed = $true }
    $script:results.Add("$id $(if ($pass) { 'PASS' } else { 'FAIL' }) - $detail")
}

# Visual Studio starts servers with its own environment: the developer shell gives the same Visual Studio variables.
# It looks for vswhere on the PATH.
$env:PATH = "$(Split-Path $vswhere);$env:PATH"
& (Join-Path $vs 'Common7\Tools\Launch-VsDevShell.ps1') -VsInstallationPath $vs -Arch amd64 -SkipAutomaticLocation | Out-Null
$server = (Get-Content (Join-Path $work '.mcp.json') -Raw | ConvertFrom-Json).servers.farol
$psi = [System.Diagnostics.ProcessStartInfo]::new($server.command)
$psi.Arguments = ($server.args | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
foreach ($property in $server.env.PSObject.Properties) { $psi.EnvironmentVariables[$property.Name] = $property.Value }
foreach ($name in @($psi.EnvironmentVariables.Keys | Where-Object { $_ -like 'Farol__UsageLog*' })) { $psi.EnvironmentVariables.Remove($name) }
$psi.WorkingDirectory = $work
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$process = [System.Diagnostics.Process]::Start($psi)
$stderr = $process.StandardError.ReadToEndAsync()
$stdin = [IO.StreamWriter]::new($process.StandardInput.BaseStream, [Text.UTF8Encoding]::new($false))
$script:nextId = 1

function Send([string]$json) { $stdin.WriteLine($json); $stdin.Flush() }
function Receive([int]$id, [int]$seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $line = $process.StandardOutput.ReadLineAsync()
        if (-not $line.Wait([Math]::Max(1000, ($deadline - [DateTime]::UtcNow).TotalMilliseconds))) { break }
        if ($null -eq $line.Result) { break }
        try { $message = $line.Result | ConvertFrom-Json } catch { continue }
        if ($message.id -eq $id) { return $message }
    }
    throw "No response to request $id in $seconds s."
}
function Request([string]$method, $params, [int]$seconds = 120) {
    $id = $script:nextId++
    Send (@{ jsonrpc = '2.0'; id = $id; method = $method; params = $params } | ConvertTo-Json -Depth 8 -Compress)
    (Receive $id $seconds).result
}
function Call([string]$tool, [hashtable]$arguments, [int]$seconds = 300) {
    $result = Request 'tools/call' @{ name = $tool; arguments = $arguments } $seconds
    [pscustomobject]@{ IsError = [bool]$result.isError; Text = (($result.content | ForEach-Object { $_.text }) -join "`n") }
}
function Show([string]$title, [string]$text, [int]$lines) { "---- $title"; ($text -split "`n" | Select-Object -First $lines) -join "`n" }

$file = Join-Path $work 'Legacy.Core\Orders\OrderCalculator.cs'
$original = [IO.File]::ReadAllText($file)
try {
    $init = Request 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'farol-vs-checks'; version = '1' } } 300
    "server: $($init.serverInfo.name) $($init.serverInfo.version), started from $work\.mcp.json"
    "Visual Studio: $vs (VisualStudioVersion=$env:VisualStudioVersion)"
    Send '{"jsonrpc":"2.0","method":"notifications/initialized"}'

    # V1: the tools the client lists.
    $tools = @((Request 'tools/list' @{}).tools | ForEach-Object name | Sort-Object)
    $expected = @($surface.tools | ForEach-Object name | Sort-Object)
    Check 'V1' (($tools -join ',') -eq ($expected -join ',')) "$($tools.Count) tools; mcp-surface.json lists $($expected.Count)"

    # V2: dotnet_overview.
    $load = Call 'dotnet_workspace' @{ action = 'load' } 600
    Show 'workspace load' $load.Text 3
    $overview = Call 'dotnet_overview' @{}
    Show 'V2 dotnet_overview' $overview.Text 14
    $missing = @('6 project(s)', 'C# 5, VB 1', 'WinForms', 'WebForms', 'ASMX', 'WCF', 'WPF') | Where-Object { -not $overview.Text.Contains($_) }
    Check 'V2' (-not $overview.IsError -and $missing.Count -eq 0) $(if ($missing) { "missing: $($missing -join ', ')" } else { '6 classic projects (5 C#, 1 VB), WinForms, WebForms, ASMX, WCF, WPF' })

    # V3: the calls to OrderCalculator.GetTotal, in C# and VB.
    $references = Call 'dotnet_find_references' @{ symbol = 'OrderCalculator.GetTotal'; kinds = 'call' }
    Show 'V3 dotnet_find_references' $references.Text 16
    $sites = [regex]::Matches($references.Text, '(?m)^- \S+\.(cs|vb):\d+')
    $languages = @($sites | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    Check 'V3' (-not $references.IsError -and $sites.Count -eq 8 -and $languages.Count -eq 2) "$($sites.Count) calls with path:line, in $($languages -join ' and ')"

    # V4: a type error in OrderCalculator.cs, then dotnet_check.
    $edited = $original.Replace('return order.Subtotal() * (1 + TaxRate());', 'return (order.Subtotal() * (1 + TaxRate())).ToString();')
    if ($edited -eq $original) { throw 'The line V4 edits is not in OrderCalculator.cs.' }
    [IO.File]::WriteAllText($file, $edited)
    $check = Call 'dotnet_check' @{}
    Show 'V4 dotnet_check' $check.Text 6
    Check 'V4' ($check.Text -match 'new since load: 1 error' -and $check.Text -match 'OrderCalculator\.cs:23 \S+ error CS0029' -and $check.Text -notmatch 'CS0168') 'only CS0029 at OrderCalculator.cs:23, not the pre-existing CS0168'
    [IO.File]::WriteAllText($file, $original)

    # V5: the migration plan to net10.0, without writing it.
    $plan = Call 'dotnet_migration_plan' @{ targetFramework = 'net10.0' } 600
    Show 'V5 dotnet_migration_plan' $plan.Text 10
    $core = $plan.Text.IndexOf('Legacy.Core', [StringComparison]::Ordinal)
    $earlier = @('Legacy.VbLib', 'Legacy.Web', 'Legacy.Desktop', 'Legacy.Wpf', 'Legacy.Tests') | Where-Object { $i = $plan.Text.IndexOf($_, [StringComparison]::Ordinal); $i -ge 0 -and $i -lt $core }
    $written = Test-Path (Join-Path $work 'docs\modernization')
    Check 'V5' (-not $plan.IsError -and $plan.Text.Contains('6 step(s)') -and $core -ge 0 -and $earlier.Count -eq 0 -and -not $written) "6 steps, Legacy.Core first, no file written"

    # V6: the prompts the client lists.
    $prompts = @((Request 'prompts/list' @{}).prompts | ForEach-Object name | Sort-Object)
    $expected = @($surface.prompts | ForEach-Object name | Sort-Object)
    Check 'V6' (($prompts -join ',') -eq ($expected -join ',')) ($prompts -join ', ')
}
catch {
    $script:failed = $true
    $results.Add("ERROR - $($_.Exception.Message)")
}
finally {
    [IO.File]::WriteAllText($file, $original)
    $stdin.Dispose()
    if (-not $process.WaitForExit(15000)) { $process.Kill(); $process.WaitForExit() }
    $problems = @($stderr.Result -split "`n" | Where-Object { $_ -match '^(fail|crit):' })
    if ($problems.Count -gt 0) { $results.Add("stderr: $($problems.Count) fail/crit line(s): $(($problems | Select-Object -First 3) -join ' | ')") }
}

'==== results'
$results
if ($script:failed -or $Keep) {
    "Kept: $work and $cache"
}
else {
    foreach ($folder in $work, $cache) {
        try { Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction Stop } catch { "Could not delete $folder`: $($_.Exception.Message)" }
    }
}
exit $(if ($script:failed) { 1 } else { 0 })

# Applies spec 001's selection rule to a calibration (baseline-only) run: a task the baseline failed at least once enters
# the set, taken in task-id order up to -PerJob per job. Runs that did not count (usage limit, interruption, isolation)
# are left out; a task needs both its calibration runs counted to be judged.
#
#   ./evals/select-calibrated.ps1 -Calibration artifacts/evals/calibration-haiku
param(
    [Parameter(Mandatory)] [string]$Calibration,
    [int]$Runs = 2,
    [int]$PerJob = 10)

$records = Get-ChildItem $Calibration -Recurse -Filter run.json | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json } |
    Where-Object { $_.arm -eq 'baseline' }
$valid = { param($r) $null -eq $r.isolationProblem -and -not $r.rateLimited -and $r.exit -ne 'cancelled' -and $null -eq $r.error }

$tasks = foreach ($group in $records | Group-Object task) {
    $counted = @($group.Group | Where-Object { & $valid $_ })
    [pscustomobject]@{
        Task = $group.Name
        Job = $group.Group[0].job
        Counted = $counted.Count
        Failed = @($counted | Where-Object { -not $_.success }).Count
    }
}

$selected = foreach ($job in $tasks | Group-Object Job) {
    $job.Group | Sort-Object Task | Where-Object { $_.Counted -ge $Runs -and $_.Failed -ge 1 } | Select-Object -First $PerJob
}

$tasks | Sort-Object Job, Task | Format-Table Job, Task, Counted, Failed -AutoSize | Out-String -Width 160
foreach ($job in $selected | Group-Object Job) { "$($job.Name): $($job.Count) selected" }
$pending = @($tasks | Where-Object { $_.Counted -lt $Runs })
if ($pending) { "not judged yet (fewer than $Runs counted runs): $(($pending | ForEach-Object Task) -join ', ')" }
'selected: ' + (($selected | Sort-Object Task | ForEach-Object Task) -join ',')

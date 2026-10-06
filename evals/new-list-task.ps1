# Writes an answer task whose reply must end with a fenced block listing files: task.json (every expected file in the
# block, no decoy in it, nothing changed) and reference.md (the block with the expected files). Expected files come from
# the compiler (evals/oracle.ps1); decoys are files that look like answers in text but are not.
#
#   ./evals/new-list-task.ps1 -Directory evals/tasks/maintain-legacy/xm01-... -Job maintain-legacy -Repo dnn-platform `
#       -Block callers -Expected <paths> -Decoys <paths>
#
# Each file is matched by its folder and name, which must single it out among the expected and decoy files.
param(
    [Parameter(Mandatory)] [string]$Directory,
    [Parameter(Mandatory)] [string]$Job,
    [Parameter(Mandatory)] [string]$Repo,
    [Parameter(Mandatory)] [string]$Block,
    [Parameter(Mandatory)] [string[]]$Expected,
    [string[]]$Decoys = @(),
    [int]$TimeoutMinutes = 20,
    [int]$MaxTurns = 60)

$ErrorActionPreference = 'Stop'
$all = @($Expected) + @($Decoys)
function Pattern([string]$path) {
    # The folder and name, with more parent folders when another expected or decoy file shares them.
    $parts = $path.Split('/')
    for ($take = [Math]::Min(2, $parts.Count); $take -le $parts.Count; $take++) {
        $suffix = $parts[($parts.Count - $take)..($parts.Count - 1)] -join '/'
        $matching = @($all | Where-Object { $_ -match ('(^|/)' + [regex]::Escape($suffix) + '$') })
        if ($matching.Count -eq 1) { break }
    }
    if ($matching.Count -ne 1) { throw "'$path' is listed twice." }
    $regex = [regex]::Escape($suffix).Replace('/', '[/\\]').Replace('\ ', ' ')
    # Inside the tagged block only: from its opening fence to the first file match, never past a closing fence.
    '(?s)```' + $Block + '(?:(?!```).)*?(?<![\w.])' + $regex
}

$checks = @(
    [ordered]@{ type = 'answer'; name = "every file in the $Block block"; patterns = @($Expected | ForEach-Object { Pattern $_ }) }
)
if ($Decoys.Count -gt 0) {
    $checks += [ordered]@{ type = 'answer'; name = "no file that only looks like one"; match = 'none'; patterns = @($Decoys | ForEach-Object { Pattern $_ }) }
}
$checks += [ordered]@{ type = 'changed'; name = 'changes nothing'; only = @() }

function Json([object]$value, [string]$indent = '') {
    $inner = $indent + '  '
    if ($value -is [string]) { return '"' + $value.Replace('\', '\\').Replace('"', '\"') + '"' }
    if ($value -is [int]) { return [string]$value }
    if ($value -is [Collections.IDictionary]) {
        $members = @($value.Keys | ForEach-Object { $inner + '"' + $_ + '": ' + (Json $value[$_] $inner) })
        return "{`n" + ($members -join ",`n") + "`n$indent}"
    }
    $items = @($value)
    if ($items.Count -eq 0) { return '[]' }
    return "[`n" + (($items | ForEach-Object { $inner + (Json $_ $inner) }) -join ",`n") + "`n$indent]"
}

$Directory = (New-Item -ItemType Directory -Force $Directory).FullName
$task = [ordered]@{ job = $Job; repo = $Repo; maxTurns = $MaxTurns; timeoutMinutes = $TimeoutMinutes; checks = $checks }
[IO.File]::WriteAllText((Join-Path $Directory 'task.json'), (Json $task) + "`n", [Text.UTF8Encoding]::new($false))
$reference = "The files, from the compiler (``evals/oracle.ps1``):`n`n``````$Block`n" + (($Expected | Sort-Object) -join "`n") + "`n```````n"
[IO.File]::WriteAllText((Join-Path $Directory 'reference.md'), $reference, [Text.UTF8Encoding]::new($false))
"wrote ${Directory}: $(@($Expected).Count) expected, $(@($Decoys).Count) decoys"

# Eval suite

The first exit criterion of [spec 001](../docs/specs/001-mvp.md): 30 tasks, 10 for each job the MVP serves, run three
times each in Claude Code with Farol and without it. With Farol, the success rate must be **at least 10 points higher**
and the tokens per task **at least 20% lower** than grep + build.

## How a run works

`Farol.Evals` creates a fresh workspace for every run: a copy of a fixture (committed as the baseline) or a git worktree
of an open-source repository at its pinned commit, prepared the way a developer's clone would be (restored, source
generators built). It then runs one headless Claude Code session in it (`claude -p`) with the task's prompt and grades
what the session left.

The two arms differ only by Farol's plugin:

| | Baseline: grep + build | Farol |
|---|---|---|
| Built-in tools | Bash, PowerShell, Read, Edit, Write, Glob, Grep, NotebookEdit, Task (subagents), task list, Skill, ToolSearch | the same |
| Added | nothing | the Farol plugin: its MCP server (the host built from this repository), its skill and its edit hook |

Nothing of the machine's own configuration reaches either arm: no user settings, installed plugins, MCP servers,
claude.ai connectors or auto-memory (`--setting-sources project,local`, `ENABLE_CLAUDEAI_MCP_SERVERS=false`,
`CLAUDE_CODE_DISABLE_AUTO_MEMORY=1`), and no session is saved. The session's init message proves it: a run whose MCP
servers or plugins differ from the arm's is reported and not counted. Web tools are off in both arms, so the tasks
stand on the repository alone. Builds may take minutes in both arms (shell and MCP timeouts are raised alike).

## What is measured

- **Success**: every check of the task passes. Checks are deterministic: patterns in the final reply, the content of
  files, the files changed since the baseline, and commands (build, tests, a behavioral script) with their exit codes.
- **Tokens per task**: every token of every model call in the session, subagents included (input, output, cache reads
  and cache writes), from Claude Code's per-model usage.
- Also reported: list-price cost, turns, minutes, and how many Farol-arm runs used Farol's tools at all.

Runs that hit the account's usage limit, were interrupted, or failed before the session started are not counted, and
the summary lists them. A session that times out is graded on what it left, like any other.

## The tasks

| Job | Fixture (`tests/fixtures`) | Open-source repository |
|---|---|---|
| Maintain legacy | `lm01`–`lm05`: callers across C#, VB and markup; methods only markup uses; a new parameter through every caller; a discount with its MSTest test; settings without leaking secrets | `dm01`–`dm05` on DNN Platform 10.3.3: implementations and injections; the callers of a method with same-named twins; a VB.NET fix checked by running it; a WebForms handler wired in code-behind; a rename across three projects |
| Migrate legacy | `lg01`–`lg05`: APIs missing on .NET 10; BinaryFormatter replaced; SDK-style project; packages.config to PackageReference; web.config to appsettings.json | `dg01`–`dg05` on DNN Platform: BinaryFormatter uses; projects already portable; obsolete WebRequest calls; a deprecated API removed; a library multi-targeted to net10.0 |
| Modern .NET | `md01`–`md05`: a null check on both target frameworks; vulnerable packages; a package upgrade; a rename with tests; implementations and DI | `em01`–`em05` on dotnet/eShop: a repository and its registration; the handlers of an integration event; a domain rename; a total that ignored discounts, with a test; the tests behind a domain rule |

Each task folder holds `prompt.md` (what a developer would ask), `task.json` (repository, limits, checks) and a known
good outcome: `reference.md` for answers, `reference.patch` for changes. `validate` proves every task before money is
spent on it: on an untouched workspace some check fails, and with the reference answer and change every check passes.

Repositories are pinned in [`repos.json`](repos.json); DNN Platform reuses the [benchmark corpus](../benchmarks/README.md).

## Running

Windows with the .NET 10 SDK, Visual Studio 2026 (DNN and the legacy fixture build with its MSBuild; the eShop
solution includes MAUI projects, so restoring it needs the MAUI workloads), git, and Claude Code signed in. Every run
is a real Claude Code session on that account: it counts against the plan's usage or the API bill.

```powershell
dotnet build Farol.slnx -c Release
$evals = "evals/Farol.Evals/bin/Release/net10.0/Farol.Evals.dll"

dotnet $evals validate                                   # prove the checks (no model calls)
dotnet $evals run --tasks lm01,md01 --runs 1             # a cheap smoke run
dotnet $evals run --out artifacts/evals/suite            # the suite: 30 tasks x 2 arms x 3 runs
dotnet $evals report --out artifacts/evals/suite         # rewrite summary.md
```

Options of `run`: `--tasks` (ids or prefixes, comma-separated), `--arms farol,baseline`, `--runs 3`,
`--model claude-sonnet-5-5`, `--concurrency 2`, `--max-cost-usd`, `--keep-workspaces true`. Arms alternate within each
round. Finished runs are kept: running again with the same `--out` resumes, which is also what to do after the usage
limit stops a suite. Each run leaves `run.json`, the session transcript (`transcript.jsonl`) and the check output in
`<out>/<task>/<arm>-<n>/`; `summary.md` aggregates them against the exit criterion. The exit code is 0 when the
criterion is met, 1 when it is not, 2 when the suite stopped early.

Workspaces live under `%LOCALAPPDATA%\Farol\evals\w` and are removed after each run.

## Results

- [2026-10-05](results/2026-10-05.md): Sonnet 5.5, 180 runs. Criterion not met: 97% success against 100%, tokens +8%.

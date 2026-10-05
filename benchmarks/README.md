# Benchmarks

AC-35 of [spec 001](../docs/specs/001-mvp.md): Farol's latency and memory targets, measured on pinned open-source
solutions. `Farol.Benchmarks` starts the host as its own process and talks MCP over stdio, the way clients do, so the
numbers include the protocol and the rendering of every answer.

| Measure | How | Target |
|---|---|---|
| Warm symbol queries | `dotnet_find_symbols`, `dotnet_symbol` and `dotnet_outline` from the corpus file; each query once to warm up, then five rounds | p95 under 100 ms |
| `dotnet_find_references` | each symbol of the corpus file once (the first one only warms the solution up): a repeated search would reuse the semantic models of the first, and agents rarely repeat one | p95 under 1 s |
| `dotnet_check` after editing one file | each edit of the corpus file written to disk and checked the way the plugin's hook does (`edited` set), then put back: bodies, new members, a changed interface signature, a rename that breaks callers in other projects | p95 under 2 s |
| Peak memory | peak working set of the server process at the end | under 3 GB |

The targets are for a warm server: after the load, the server builds every compilation and its search indexes in the
background, and calls are timed once `dotnet_workspace` reports that warm-up done (on a 4-core machine, a call made
during it shares the processors with it). The report also gives the time to answer `tools/list` after the process
starts (it must not wait for the load), the load time and the warm-up time.

## Corpora

| Corpus | Commit | Size | Why |
|---|---|---|---|
| [`umbraco.json`](corpora/umbraco.json) | Umbraco CMS 18.2.0 | 0.77M lines of C#, 32 net10.0 projects | the largest single-target open-source .NET solution that restores from nuget.org with the stock SDK; larger ones multi-target (which multiplies the compilations loaded) or need their own SDK and workloads |
| [`dnn-platform.json`](corpora/dnn-platform.json) | DNN Platform 10.3.3 | 0.58M lines of C# and VB, 80 projects, .NET Framework 4.8 and WebForms | the legacy side, and the legacy repository of the [eval suite](../evals/README.md) |

Each corpus file pins the commit, says how to prepare the checkout (`prepare`) and lists the calls to time. The
queries name real symbols, files and edits of that commit; an ambiguous name or an edit whose text is not found exactly
once fails the run rather than timing the wrong thing.

## Running

On Windows with the .NET 10 SDK and Visual Studio 2026 (DNN restores and builds with Visual Studio's MSBuild):

```powershell
./benchmarks/prepare.ps1 -Corpus umbraco
dotnet build benchmarks/Farol.Benchmarks -c Release
dotnet benchmarks/Farol.Benchmarks/bin/Release/net10.0/Farol.Benchmarks.dll --corpus benchmarks/corpora/umbraco.json --repo $env:LOCALAPPDATA/Farol/evals/repos/umbraco
```

`prepare.ps1` checks the corpus out at its commit (by default under `%LOCALAPPDATA%\Farol\evals\repos`, which the eval
suite shares) and runs its preparation steps. They restore with `NuGetAudit=false`: advisories published after the
pinned commit would fail its restore, since warnings are errors there, and they say nothing about Farol.

The results go to `artifacts/benchmarks/<corpus>/`: `report.md`, `results.json` (every timed call), the server's log
and its load status. The exit code is 0 when every target is met, 1 when one is missed or a call failed.

## CI

[`.github/workflows/benchmarks.yml`](../.github/workflows/benchmarks.yml) runs both corpora on `windows-2025` (4 cores,
16 GB, Visual Studio 2026) on every push to `main` that touches `src/` or `benchmarks/`, every Monday, and on demand.
Each job fails when a target is missed, and its summary is the report. Job logs and summaries need a signed-in GitHub
user; the results and each missed target are also annotations of the run, which anyone can read (the web page or
`GET /repos/yuriafp/Farol/check-runs/<job id>/annotations`).

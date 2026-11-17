# Spec 002: Farol Phase 2 — speed, reach and depth

**Status:** Approved (2026-11-17)
**Date:** 2026-11-17

> Source: the items spec 001 left for Phase 2 (persistent index, alternative loaders, project analyzers in `check`, the
> MCP Registry) and three items it left out of the MVP (dedicated refactorings, project conversions, structured output),
> all chosen by the developer on 2026-11-17, plus `/spec` questioning rounds. Every developer answer is a requirement.

## Process log

- 2026-11-17 — **Scope chosen by the developer**: all seven items above. Round 1: one release at the end of the phase
  (not a release per item); the index lives in the user's local application data, per solution, never in the
  repository; the alternative loaders target Windows without Visual Studio or Build Tools, and Linux (CI and dev
  containers), with macOS following Linux without dedicated testing; refactorings and conversions follow
  `dotnet_code_actions`: a diff by default, `apply=true` writes and returns a fresh check, refused in `--read-only`.
- 2026-11-17 — **Round 2.** While a solution loads in a new session, the navigation tools (`dotnet_find_symbols`,
  `dotnet_find_references`, `dotnet_symbol`, `dotnet_outline`, `dotnet_hierarchy`, `dotnet_call_hierarchy`) and
  `dotnet_overview` answer from the index, marking what may be out of date; `dotnet_check`, `dotnet_code_actions`,
  build and test wait for the load. Dedicated refactorings: `rename`, `change_signature`, `move_type` and
  `extract_method`. Structured output on all 18 tools (and the new ones), with the text unchanged for clients that read
  only text. Project analyzers in `dotnet_check` are off by default: `analyzers=true` on a call, or an option
  (`Farol:CheckAnalyzers`) that turns them on for every check, the hook's included.
- 2026-11-17 — **Round 3.** Files changed since the index was written (by time and size) do not stop an answer from
  the index: it lists them, and marks the results located in them as possibly out of date. Without an MSBuild that can
  evaluate .NET Framework projects, the heuristic loader takes over on its own (project files, packages, reference
  assemblies) and says that its fidelity is lower; the binary-log loader is used when a `.binlog` is named (an option
  or the `workspace`). Project conversion changes the format only: classic project to SDK-style keeping its target
  framework, and packages.config to PackageReference; retargeting stays a step of the migration plan. Target: with the
  index of a large solution on disk, p95 of 2 s from the server's start to the first `dotnet_find_symbols` answer,
  measured by the benchmarks on their corpora.
- 2026-11-17 — **Round 4.** An index unused for 30 days is deleted, as the usage log is; `Farol:Index=false` stops
  writing and reading it. The MCP Registry listing is published by a GitHub Actions workflow (OIDC), not by hand.
  `dotnet_check` with analyzers: p95 under 4 s after editing one file, twice the target without them; the answer says
  how long the analyzers took. `rename` of a type renames its file when the file has the type's name (with a WebForms
  or XAML code-behind, its markup too), as Visual Studio does; `renameFile=false` keeps the file.
- 2026-11-17 — **Round 5.** The Registry workflow publishes when the GitHub release is published, without waiting for
  nuget.org. CI gains a Linux job, where the legacy fixtures load through the heuristic loader, next to the Windows job
  with full fidelity. `dotnet_check` works on a heuristic load, and says that the load is heuristic and that build and
  test need MSBuild: the delta against the baseline still holds, since the load's false errors are in the baseline.
  Exit criteria besides the acceptance criteria: the CI benchmarks meet the new targets and the old ones; a new eval
  round with the new tools against grep + build; a release smoke test on Linux with a legacy solution.
- 2026-11-17 — **Round 6.** Output schemas join the `mcp-surface.json` snapshot: adding a field is compatible, removing
  or renaming one needs a new major version, as tool names and parameters already do. The refactorings cover C# and
  VB wherever Roslyn offers them for both; one that exists only for C# is an `invalid_argument` error on VB. The
  heuristic loader takes the .NET Framework reference assemblies from the `Microsoft.NETFramework.ReferenceAssemblies`
  package in the NuGet cache, restored on the first load; in `--offline` only from the cache, and otherwise the load
  says what is missing. One tool per refactoring: `dotnet_rename`, `dotnet_change_signature`, `dotnet_move_type`,
  `dotnet_extract_method`, and `dotnet_convert_project` (`kind=sdk_style|package_reference`): 23 tools.
- 2026-11-17 — **Draft written, In Review.** Items marked **(proposed)** fill gaps the rounds did not ask about.
- 2026-11-17 — **Approved by the developer**, with the **(proposed)** items as written. One correction of fact at
  approval: the benchmarks have no synthetic solution; the startup target is measured on their corpora (Umbraco, 0.77M
  lines, and DNN Platform, 0.58M lines of C# and VB).

## Problem

Phase 1 made Farol answer from the compiler for legacy and modern .NET, and close the edit → check loop. Three limits
remain, seen in real use and in the smoke tests:

- **Speed.** Every session loads the solution again: 4–5 s on the fixtures, 30–70 s on a large solution, during which
  navigation waits and agents fall back to grep.
- **Reach.** Legacy projects load with full fidelity only on Windows with Visual Studio or Build Tools; CI, Linux and
  dev containers get reduced fidelity. Discovery is limited to nuget.org and the Claude Code plugin.
- **Depth.** Farol tells an agent what is wrong but changes code only through `dotnet_code_actions`; the migration plan
  says to convert projects but nothing converts them; `dotnet_check` misses analyzer rules that fail the build; answers
  are text that a client cannot read as data.

## Scope

### In

1. **Persistent index** in the user's local application data, per solution: navigation and `dotnet_overview` answer
   from it while a new session loads.
2. **Alternative loaders:** heuristic (automatic without an MSBuild that can evaluate .NET Framework projects) and
   binary log (when a `.binlog` is named), on Windows and Linux.
3. **Project analyzers in `dotnet_check`**, opt-in.
4. **MCP Registry listing**, published by a workflow.
5. **Dedicated refactorings:** `dotnet_rename`, `dotnet_change_signature`, `dotnet_move_type`, `dotnet_extract_method`.
6. **Project conversion:** `dotnet_convert_project`, format only.
7. **Structured output** on every tool.
8. A Linux CI job; one release, 0.2.0 **(proposed)**, at the end of the phase.

### Out

- HTTP server, proxy mode, multi-user and OAuth (Phase 3).
- Runtime diagnostics, MCP Apps, semantic search, F#, debugger.
- Retargeting projects (`net48` → `net10.0`) as a tool: it stays a migration-plan step.
- Other refactorings (introduce variable, inline, pull members up) beyond `dotnet_code_actions`.
- A shared or committed index; an index server.
- macOS CI (macOS follows Linux without dedicated testing).

## Domain Model

- **Index** — per solution, a SQLite file under `Farol/index/<hash of the solution's full path>/` in the local
  application data folder **(proposed)**. It holds, for the last load: the projects (as `dotnet_overview` shows them),
  the declarations with their documentation comment IDs and locations, the references between symbols with their
  locations (code and markup), the type hierarchy, the call graph, and each source file's time and size. It holds no
  source text beyond the one line shown with a reference **(proposed)**. Written after a load and its warm-up are done,
  and updated in the background as snapshots change **(proposed)**; read when a session starts, before the load.
  States: absent → written → stale (files changed since) → replaced. Deleted after 30 days unused.
- **Index answer** — a navigation answer served from the index while the load runs: it says so, lists the files
  changed since the index was written, and marks the results located in them.
- **Loader** — `msbuild` (Phase 1, full fidelity), `heuristic`, or `binlog`. A workspace records which loader loaded it,
  and every answer from a non-MSBuild load says so, in the line that names the workspace **(proposed)**.
- **Refactoring** — a change computed on a snapshot: a set of file edits, renames and moves, shown as a unified diff,
  then applied on request with a fresh check, like a code action.
- **Conversion** — a project-file rewrite (and `packages.config` removal) shown as a diff, then applied.

## API Contract

The conventions of spec 001 hold (paths relative to the root, symbol forms, token budget, error codes, annotations).

| Tool | Parameters (new) | Writes | Answer |
|---|---|---|---|
| `dotnet_rename` | `symbol`, `newName`, `renameFile` (default true), `apply` (default false) | with `apply` | diff of every file (code and markup) and the file rename; with `apply`, the files written and a fresh check |
| `dotnet_change_signature` | `symbol`, `parameters` (the new ordered list: existing names, and new ones with a type and a call-site value) , `apply` | with `apply` | diff of the declaration, overrides, implementations and call sites |
| `dotnet_move_type` | `symbol`, `toFile` and/or `toNamespace`, `apply` | with `apply` | diff, including the usings/imports added at use sites |
| `dotnet_extract_method` | `path`, `startLine`, `endLine`, `name`, `apply` | with `apply` | diff of the new method and the call |
| `dotnet_convert_project` | `project`, `kind` (`sdk_style` or `package_reference`), `apply` | with `apply` | diff of the project file and the removed files; what could not be converted, and why |
| `dotnet_check` | `analyzers` (default from `Farol:CheckAnalyzers`, false) | no | as today, plus analyzer diagnostics and the time the analyzers took |

- Every tool gains an `outputSchema` and returns `structuredContent` with the same facts as its text, which does not
  change. The schemas are in the surface snapshot.
- New options: `Farol:Index` (default true), `Farol:CheckAnalyzers` (default false), `Farol:Binlog` (a `.binlog` path)
  **(proposed name)**; `workspace` also accepts a `.binlog`.
- Write tools are refused in `--read-only` with `read_only`, carry `destructiveHint` false and `idempotentHint` false
  **(proposed)**, and refuse to apply when a file they touch changed since the diff's snapshot (`conflict`)
  **(proposed)**.

## Authorization

As in spec 001: the root and `Farol:TrustedPaths` only, after resolving links; write tools are refused in `--read-only`.
The index directory is Farol's own and is outside those rules **(proposed)**. A `.binlog` must be inside the trusted
directories, since it names files Farol then reads **(proposed)**.

## Edge Cases & Failure Modes

- **Two servers, one solution:** the index is written to a temporary file and moved into place; readers take the last
  complete one; a corrupt or unreadable index is deleted and ignored **(proposed)**.
- **Index from another Farol version:** ignored and rewritten when its format version differs **(proposed)**.
- **Files changed since the index:** answered and marked (round 3); a project-file change since the index makes the
  index answer say that the projects may have changed **(proposed)**.
- **Heuristic load:** source generators and design-time targets do not run, so generated code is missing; the load
  says which projects use generators **(proposed)**. Build and test say they need MSBuild.
- **Binlog older than the sources:** loads, and the answer says the binlog is older than N files **(proposed)**.
- **Refactoring conflicts:** a name that already exists, or a rename that changes overload resolution, is an error with
  the conflicting locations, and nothing is written **(proposed)**.
- **Conversion of an unsupported project** (a WebForms web application project, custom targets the converter cannot
  keep): the diff lists what is left, or the tool refuses with the reason **(proposed)**.
- **An analyzer that throws or hangs:** the exception is reported as an analyzer failure; analyzers stop at a time limit
  and the answer says which did not finish **(proposed)**.
- **Registry publish failure:** the workflow fails visibly; the package release is not affected.

## Non-Functional Requirements

- **Startup with an index:** p95 under 2 s from the server's start to the first `dotnet_find_symbols` answer on the
  benchmark corpora (Umbraco and DNN Platform).
- **`dotnet_check` with analyzers:** p95 under 4 s after editing one file.
- The Phase 1 targets still hold. Index size: under 200 MB for about 1M lines of code **(proposed)**.
- **Platform:** Windows and Linux in CI; legacy loads heuristically on Linux.
- **Privacy:** the index stays on the machine; it holds symbol names and locations of the user's code, and nothing
  leaves it.

## Integrations

- **MCP Registry:** a workflow publishes `server.json` (namespace `io.github.yuriafp`) with GitHub OIDC when the GitHub
  release is published.
- **NuGet:** `Microsoft.NETFramework.ReferenceAssemblies` for heuristic loads.
- **Claude Code plugin:** the contract is unchanged; the hook reports analyzer diagnostics when `Farol:CheckAnalyzers`
  is on.

## Acceptance Criteria

Numbering continues from spec 001.

### Index

- **AC-50** — Given a solution loaded once with the index on, when a new server starts on it, then
  `dotnet_find_symbols`, `dotnet_find_references`, `dotnet_symbol`, `dotnet_outline`, `dotnet_hierarchy`,
  `dotnet_call_hierarchy` and `dotnet_overview` answer before the load is done and say that the answer comes from the
  index and when it was written; `dotnet_check` waits for the load.
- **AC-51** — Given an index, when a source file changed after it was written, then index answers list that file and
  mark the results located in it; once the load is done, answers come from the load.
- **AC-52** — Given `Farol:Index=false`, then no index is read or written. Given an index unused for 30 days, then a
  later server deletes it. Given a corrupt index, or one of another format version, then it is ignored and replaced.
- **AC-53** — Given each benchmark corpus indexed, then the benchmarks measure p95 under 2 s from the server's
  start to the first `dotnet_find_symbols` answer.

### Loaders

- **AC-54** — Given the legacy fixture on Linux (or on Windows without Visual Studio or Build Tools), when it loads,
  then the heuristic loader loads all its projects, `dotnet_overview` says that the load is heuristic, and navigation
  finds the C# and VB call sites of `OrderCalculator.GetTotal` that AC-6 lists.
- **AC-55** — Given a heuristic load, when a file is edited, then `dotnet_check` reports the new error and says that the
  load is heuristic; `dotnet_build` and `dotnet_test` say that they need MSBuild.
- **AC-56** — Given a `.binlog` of the legacy fixture named as `workspace` or `Farol:Binlog`, then the workspace loads
  from it with its projects, references and compiler options, and says so; a binlog older than the sources says so.
- **AC-57** — Given `--offline` and no reference assemblies in the cache, then a heuristic load says what is missing.

### Analyzers

- **AC-58** — Given a project whose analyzer reports a rule as an error (a CA rule with `TreatWarningsAsErrors`, for
  example), when an edit breaks the rule and `dotnet_check` runs with `analyzers=true`, then the rule is reported as
  new; without it, it is not; with `Farol:CheckAnalyzers=true`, the hook reports it.
- **AC-59** — Given `analyzers=true`, then the benchmarks measure p95 under 4 s after editing one file, and the answer
  says how long the analyzers took.

### Refactorings

- **AC-60** — Given `OrderCalculator.GetTotal`, when `dotnet_rename` runs with `newName='CalculateTotal'`, then the diff
  changes the declaration and the C#, VB and markup references and writes nothing; with `apply=true` it writes them and
  returns a check with no new error.
- **AC-61** — Given a type in a file of the same name, when it is renamed, then the file is renamed too (and the markup
  of a code-behind); with `renameFile=false` it is not.
- **AC-62** — Given a method with callers, when `dotnet_change_signature` adds a parameter with a call-site value and
  reorders the others, then the declaration, overrides and call sites change in C# and VB, and the check is clean.
- **AC-63** — Given a type, when `dotnet_move_type` moves it to another namespace, then its uses compile, with the
  usings/imports added, and the check is clean.
- **AC-64** — Given a range of statements, when `dotnet_extract_method` runs, then a new method holds them, the range
  calls it, and the check is clean.
- **AC-65** — Given a rename to a name that already exists in scope, then it is an error that names the conflict, and no
  file is written. Given `--read-only`, every write tool refuses `apply=true` with `read_only`, and a diff still works.

### Conversion

- **AC-66** — Given `Legacy.Core` (classic), when `dotnet_convert_project` runs with `kind=sdk_style`, then the diff
  turns it into an SDK-style project still targeting net48; after `apply=true`, the workspace reloads it with the same
  files and references and no new error.
- **AC-67** — Given `Legacy.Tests` (packages.config), when it runs with `kind=package_reference`, then the packages
  become PackageReference items, `packages.config` is removed, and the reload resolves the same packages.

### Structured output and distribution

- **AC-68** — Given any tool, then its result has `structuredContent` that validates against its `outputSchema`, and the
  text is the one Phase 1 returns; the schemas are in `mcp-surface.json`.
- **AC-69** — Given a published GitHub release, then the Registry workflow publishes `server.json` with the release's
  version, and a test checks `server.json` against the Registry's schema.
- **AC-70** — Given CI, then a Linux job runs the suite with the legacy fixtures loaded heuristically, next to the
  Windows job.

## Deferred Decisions

- **The release's version:** 0.2.0 by default (new tools and compatible changes); revisit if a breaking change appears.
- **Index updates while a session runs:** written after the load and in the background afterwards (proposed); revisit
  if the benchmarks show the background writes cost `check` latency.

## Open Questions

None.

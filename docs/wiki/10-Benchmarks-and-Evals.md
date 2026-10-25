# Benchmarks & Evaluations

Farol is rigorously validated using two automated evaluation systems:
1. **Performance Benchmarks (`Farol.Benchmarks`)**: Continuous verification of latency, throughput, and memory consumption on pinned, large-scale open-source .NET solutions in CI.
2. **Empirical Agent Evals (`Farol.Evals`)**: Headless evaluation testing whether AI coding agents perform tasks with higher accuracy and lower token consumption using Farol compared to standard `grep` + `build`.

---

## 1. Performance Benchmarks (`Farol.Benchmarks`)

AC-35 of [Spec 001](https://github.com/yuriafp/Farol/blob/main/docs/specs/001-mvp.md) sets strict latency and memory targets for Farol. `Farol.Benchmarks` spawns Farol as an out-of-process server over stdio and drives queries over the protocol, ensuring metrics include JSON-RPC serialization and text rendering.

### Benchmark Corpora

| Corpus | Version / Commit | Size | Rationale |
|---|---|---|---|
| **Umbraco CMS** | `18.2.0` | 0.77M lines C#, 32 `net10.0` projects | The largest single-target open-source modern .NET solution that restores from nuget.org with the standard SDK. |
| **DNN Platform** | `10.3.3` | 0.58M lines C# & VB, 80 projects, .NET Framework 4.8 & WebForms | The gold-standard enterprise legacy codebase combining C#, VB.NET, classic projects, and WebForms markup. |

### Targets & CI Results (4-core `windows-2025` runners)

| Metric | Benchmark Method | Spec Target | Measured in CI (p95, both corpora) |
|---|---|---|---|
| **Warm Symbol Queries** | `dotnet_find_symbols`, `dotnet_symbol`, `dotnet_outline` (warm-up run + 5 timed rounds) | **p95 < 100 ms** | **43 ms or less** |
| **Reference Finding** | `dotnet_find_references`, each symbol of the corpus once (e.g. `IPublishedContent` with about 1,500 uses) | **p95 < 1 s** | **579 ms or less** |
| **Edit Verification** | `dotnet_check` after an edit, the way the plugin's hook calls it: bodies, new members, a changed interface signature | **p95 < 2 s** | **1.34 s or less** |
| **Peak Memory** | Peak working set of the Farol process | **< 3 GB** | **1.33 GB or less** |

*Measured at 0.1.0-alpha.1 ([release notes](https://github.com/yuriafp/Farol/releases/tag/v0.1.0-alpha.1)); the workflow ([`.github/workflows/benchmarks.yml`](https://github.com/yuriafp/Farol/blob/main/.github/workflows/benchmarks.yml)) fails when a target is missed.*

---

## 2. Empirical Agent Evaluations (`Farol.Evals`)

The primary MVP exit criterion required proving that an AI coding agent achieves **at least 10 percentage points higher success** and consumes **at least 20% fewer tokens** when equipped with Farol versus standard `grep` + `build`.

### Evaluation Harness Design
`Farol.Evals` provisions isolated, fresh workspaces for every run (using git worktrees or fixture copies) and runs headless Claude Code sessions (`claude -p`) under two controlled arms:

| Harness Arm | Available Capabilities |
|---|---|
| **Baseline (grep + build)** | Claude Code's built-in tools (shell, file read/edit/write, search). No MCP servers. |
| **Farol** | The exact same tools + the Farol plugin (`Farol.Mcp` server, edit verification hook, modernization skill). |

Both arms run with web tools off and none of the machine's own configuration (user settings, plugins, MCP servers, memory), so the tasks stand on the repository alone.

---

## 3. The Evaluation Tasks

There are two task sets of 30 tasks each, across the three jobs (maintain legacy, migrate legacy, modern .NET).

- **The first suite** (`lm`/`dm`, `lg`/`dg`, `md`/`em`): tasks on Farol's fixtures, DNN Platform and dotnet/eShop. Its first run (2026-10-05, Claude Sonnet 5.5) did not meet the criterion: grep + build solved 100% of the tasks and Farol 97%, with 8% more tokens. The tasks were too easy to separate the arms.
- **The harder set** (`lg`/`lm`, `xg`/`xm`, `xu`): selected by calibrating grep + build alone, under a rule recorded in the spec before any run. 4 tasks on the legacy fixture, 16 on DNN Platform and 10 on Umbraco CMS, most of them generated lists of callers or writers with decoys, answered by the compiler oracle (section 5).

### Deterministic Grading
A run succeeds when every check of its task passes. Checks are deterministic: patterns in the final reply, the content of files, the files changed since the baseline, and commands (build, tests, a behavioral script) with their exit codes.

---

## 4. Empirical Results: Farol vs. Baseline

### Claude Haiku 4.5 on the harder set ([report](https://github.com/yuriafp/Farol/blob/main/evals/results/2026-10-07-haiku.md))
Measured across 180 valid runs (3 fresh runs per arm across 30 calibrated tasks):

```
Success rate:
  Baseline (grep + build): 28% (25/90)  ██████
  Farol:                   59% (53/90)  █████████████ (+31 percentage points)

Tokens per task:
  Baseline (grep + build): 509k
  Farol:                   346k  (-32%)
```

- **Success**: Farol outperformed the baseline by **+31 percentage points** (59% vs 28%), three times the required +10 points.
- **Tokens**: Farol used **32% fewer tokens** per task, beating the 20% reduction target.
- **By repository**: the gain came from DNN Platform, where Farol succeeded in **83%** of runs (40/48) against **27%** (13/48) for grep + build. On Umbraco CMS both arms mostly failed (20% vs 10%), and the Farol arm used its tools in only 3 of 30 runs; on the legacy fixture grep + build did better (75% vs 58%).

---

## 5. The Compiler Oracle Methodology (`oracle.ps1`)

Reference answers for the list tasks come from a **compiler oracle**, not from Farol:

1. **Member Deprecation (`oracle.ps1`)**:
   The target member or overload is programmatically annotated with `[Obsolete]`.
2. **Compiler Harvest**:
   The solution is built, with those warnings kept as warnings. The compiler emits `CS0618` (C#) and `BC40000` (VB.NET) warnings at every exact call site.
3. **Decoy Generation (`new-list-task.ps1`)**:
   `new-list-task.ps1` writes the task from the compiler's expected files plus decoys: files that contain the same text without a use (e.g. a same-named method or overload on another type). A correct answer must not list them.
4. **Oracle Transparency**:
   The compiler oracle has one known edge case: it does not emit warnings for obsolete members called inside code that is itself marked `[Obsolete]`. The results quantify this blind spot.

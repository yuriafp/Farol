# Phase 2 plan — spec 002

Implements [spec 002](../specs/002-phase-2.md) (approved 2026-11-17). As in Phase 1, each slice ends green: build without
warnings, all tests passing, acceptance criteria covered by tests named after them. The phase ships as one release at the
end (0.2.0), after the exit criteria.

| # | Slice | Acceptance criteria | Status |
|---|---|---|---|
| 0 | Spike: Roslyn's refactorings without an editor (rename through `Renamer`; change signature, extract method and move type through their refactoring providers and the option services they ask for), and a heuristic load of the legacy fixture on Linux. Results decide the shape of slices 2, 3 and 6 | — | done: rename and extract method use Roslyn; change signature and move type are Farol's own ([results](../spikes/phase-2-results.md)) |
| 1 | Structured output: an `outputSchema` and `structuredContent` for the 18 tools, built from the same result objects as the text; schemas in `mcp-surface.json`; first, so the new tools have them from the start | AC-68 | done |
| 2 | Write tools and `dotnet_rename`: the shared diff → `apply` → fresh check path of `dotnet_code_actions`, the `conflict` refusal for files changed since the diff, `read_only`; rename across C#, VB and markup, with the file (and code-behind markup) | AC-60, AC-61, AC-65 | done |
| 3 | `dotnet_change_signature`, `dotnet_move_type`, `dotnet_extract_method`, C# and VB | AC-62 – AC-64 | done |
| 4 | `dotnet_convert_project`: classic → SDK-style keeping the target framework, packages.config → PackageReference, then a reload | AC-66, AC-67 | done |
| 5 | Project analyzers in `dotnet_check` (`analyzers`, `Farol:CheckAnalyzers`, the hook), with a time limit and the analyzers' time in the answer; a fixture project with a CA rule as error | AC-58, AC-59 | done (benchmark p95 measured at slice 10) |
| 6 | Heuristic loader: project files, packages, reference assemblies from `Microsoft.NETFramework.ReferenceAssemblies`, markup; chosen when no MSBuild can evaluate .NET Framework projects; `check` with the note, build and test refused; Linux CI job | AC-54, AC-55, AC-57, AC-70 | done |
| 7 | Binary-log loader (`workspace` or `Farol:Binlog`), from the compiler invocations the log records | AC-56 | not started |
| 8 | Persistent index: SQLite store, written after the load and warm-up and kept up to date in the background, read at startup; index answers for navigation and `dotnet_overview`, with the files changed since; retention, `Farol:Index`, corrupt or old indexes; startup benchmark on both corpora | AC-50 – AC-53 | not started |
| 9 | MCP Registry: a workflow publishes `server.json` with GitHub OIDC on a published release; a test checks `server.json` against the Registry's schema | AC-69 | not started |
| 10 | Exit and release: benchmarks meet the new and old targets; an eval round with the new tools against grep + build; a release smoke test on Linux with a legacy solution; docs, wiki and 0.2.0 | exit criteria | not started |

## Order

Slice 1 comes first because every later tool returns structured output, and changing all 18 tools at once is easier
before five more join them. The refactorings (2–4) share the write path and come next. Analyzers (5) are independent.
The loaders (6, 7) come before the index (8), so the index records which loader made it, and the startup benchmark
covers a heuristic load too. The Registry (9) has no code dependency and can move earlier if a release workflow change
is convenient.

## Risks

- **Editor-only refactorings.** Change signature and extract method live in Roslyn's Features layer behind option
  services meant for a dialog. The spike (slice 0) checks that providing those services is enough, through the MEF host
  `dotnet_code_actions` already builds. If one needs internal APIs, the fallback is our own implementation on the
  syntax and semantic model for that refactoring, recorded in the spec log.
- **Heuristic fidelity.** Without design-time builds, conditions, imports and generated code are approximations. AC-54
  and AC-55 fix what must work; the rest is reported as reduced fidelity, not hidden.
- **Index correctness.** An index answer can be wrong for files changed since; AC-51 makes it say so. The background
  writes must not cost `check` latency: the benchmarks watch it (Deferred Decisions in the spec).
- **One release at the end.** Many changes ship together; each slice keeps the suite and the benchmarks green on main, and
  the release smoke covers every new tool.

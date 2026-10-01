# Phase 1 plan — spec 001

Implements [spec 001](../specs/001-mvp.md) (approved 2026-09-30). Each slice ends green: build without warnings, all tests passing, acceptance criteria covered by tests named after them.

| # | Slice | Acceptance criteria | Status |
|---|---|---|---|
| 0 | Foundation, spikes, `workspace`, `overview` | AC-1 – AC-4, AC-29 (partial), AC-31 | done |
| 1 | Navigation core and file sync: `find_symbols`, `symbol`, `find_references`, `hierarchy`, `call_hierarchy`, `outline`; snapshots refresh from file changes | AC-5, AC-6, AC-9 – AC-13 | done |
| 2 | Markup index (.aspx/.ascx/.master/.svc/.asmx/XAML) wired into `find_references` | AC-7, AC-8 | done (78 tests green with VS 2026 18.11) |
| 3 | Verify: `check` with the load baseline, `code_actions` (diff and apply), trust and path sandbox, `--read-only` | AC-14 – AC-18, AC-32 | done (106 tests green) |
| 4 | Build and test: test fixtures, `build`, `test` (affected tests), long operations as tasks | AC-19 – AC-22 | |
| 5 | Packages: packages.config fixture, `packages`, `package_api` | AC-23, AC-24 | |
| 6 | Legacy: `legacy_inventory`, `config_inspect`, `portability`, `migration_plan` | AC-25 – AC-28 | |
| 7 | Plugin and distribution: Claude Code plugin (skill, hook, prompts), NuGet `McpServer` package, client docs, smoke tests | AC-33, AC-34, AC-36 | |
| 8 | Evals and benchmarks: CI performance benchmarks, 30-task eval suite (DNN Platform, dotnet/eShop, fixtures) | AC-35, exit criteria | |

Cross-cutting, checked in every slice: AC-29 (tool conventions), AC-30 (relative paths), AC-31 (clean stdout).

## Notes

- **Slice 1:** Roslyn 5.9 appends `~ReturnType` to every method's documentation comment ID. Farol normalizes IDs to the standard form (the suffix is kept only for conversion operators) and accepts both on input.
- **Slice 1:** Changes are applied lazily before the next request. Source edits update the snapshot in place, new files join SDK-style projects without a reload, and project-file changes reload. External (metadata) symbols have no source yet; SourceLink/decompilation lands with the packages slice.
- **Slice 3, `check`:** compiler diagnostics only; analyzer rules (CA/IDE) would cost seconds per check and come with `build` in slice 4. Edits that leave a file's declarations unchanged (top-level-equivalent syntax trees) re-check only the edited files; declaration changes re-check the project and every project that depends on it; `includeExisting` checks whole projects. A diagnostic is matched to the baseline by id, file, message and the text of its line, so line shifts do not make it new.
- **Slice 3, load snapshot:** Roslyn reads document text lazily, on first use, so a file edited before anything read it leaked into the load snapshot, which made the edit invisible both to file sync and to the `check` baseline. The loader now reads every document's text during the load.
- **Slice 3, `code_actions`:** Roslyn's built-in fixes and refactorings (`Microsoft.CodeAnalysis.CSharp.Features` and `VisualBasic.Features` 5.9), created by reflection. Actions that need a dialog are skipped. An optional `column` input, beyond the spec's key inputs, narrows refactorings to one position. `apply` refuses actions that change references or project files, refuses adding or removing files in classic projects (their project file lists every file), and refuses to write when the file changed on disk after the snapshot.
- **Slice 3, English output:** satellite resource assemblies are left out of the build (`SatelliteResourceLanguages=en`), so compiler messages and action titles are English on any OS language. MSBuild's own load messages can still follow the OS language; revisit with `build` and `test` in slice 4.
- **Test isolation:** the two test processes run in parallel and design-time loads write `obj/`. Tests that mutate files or run in the MCP test process use temp copies of the fixtures (`FixtureCopy`). Engine tests that load `tests/fixtures` directly share one load per fixture in a serialized collection.

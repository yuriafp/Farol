# Phase 2 spike results (slice 0)

Slice 0 of the [Phase 2 plan](../plans/phase-2.md), on 2026-11-17, with Microsoft.CodeAnalysis 5.12 on the legacy
fixture (`tests/Farol.Engine.Tests/Phase2RefactoringSpikeTests.cs`, report in `artifacts/spikes/phase-2-refactorings.md`).

## Refactorings without an editor

| Refactoring | What Roslyn offers | Decision |
|---|---|---|
| rename | the public `Renamer.RenameSymbolAsync`: `GetTotal` → `CalculateTotal` changed 8 documents, C# and VB, matching the 8 references | use `Renamer`; markup (WebForms, XAML) and the file rename are Farol's own edits on top, from its markup index |
| extract method | `ExtractMethodCodeRefactoringProvider` offers "Extract method" and "Extract local function" as plain actions on a statement range; applying one changes the document | use the provider, then `Renamer` from the generated name to the requested one |
| change signature | `ChangeSignatureCodeRefactoringProvider` offers "Change signature..." only as a `CodeActionWithOptions`, whose options come from an internal dialog service | Farol's own implementation on public APIs: the declaration, overrides and implementations (`SymbolFinder`), and the argument lists of every call site, named arguments included |
| move type | "Move type to file" is offered only when the file's name or contents differ from the type; "Move to namespace" is a `CodeActionWithOptions` | Farol's own implementation: the type into a new or existing file and/or namespace, with `ImportAdder` adding usings/imports at the use sites |

## Alternative loaders

- The .NET Framework 4.8 reference assemblies (`Microsoft.NETFramework.ReferenceAssemblies.net48` 1.0.3) are in the
  NuGet cache of a machine that restored the fixtures, as the spec expects.
- `Basic.CompilerLog` already rebuilds the legacy fixture's compilations from a binary log (Phase 0, spike A), with the
  VB `/sdkpath` workaround: the binary-log loader starts from it.
- A Linux environment for the heuristic loader is the CI job of slice 6 (no local Linux SDK image was pulled).

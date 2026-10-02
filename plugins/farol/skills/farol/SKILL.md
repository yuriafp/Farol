---
name: farol
description: >
  How to work in .NET (C# and VB) solutions with the Farol MCP server, legacy .NET Framework and modern .NET alike:
  compiler-accurate navigation instead of grep, checking edits in about a second instead of building, running only the
  tests an edit reaches, reading the exact API of a NuGet package version, and planning .NET Framework migrations.
  Load this skill whenever the working directory holds a .sln, .slnx, .csproj or .vbproj, before searching C#/VB code,
  after editing .cs/.vb files, before adding or upgrading a NuGet package, or when the user mentions .NET Framework,
  WebForms, WCF, ASMX, WinForms, WPF, packages.config, portability or modernization.
---

# Working in .NET with Farol

Farol answers from the compiler (Roslyn and MSBuild), not from text. Its tools start with `dotnet_` and return compact
text with `path:line` references relative to the workspace root. Prefer them over grep, whole-file reads and full
builds whenever the question is about C# or VB code.

## Start

- In an unfamiliar solution, call `dotnet_overview` once: projects, languages, target frameworks, app models (ASP.NET
  Core, WebForms, WCF, WinForms, WPF…), test frameworks and the build command that works.
- `dotnet_workspace` reports the load state and projects that failed to load. Large solutions take a while to load
  the first time; other tools wait for it.

## Find and read code

| Need | Tool |
|---|---|
| Where is X declared? | `dotnet_find_symbols` (exact, prefix, substring, camel-case humps: `OrdCalc` → `OrderCalculator`) |
| What is X, and its docs or code? | `dotnet_symbol` with `include='docs,source'`; a type returns a skeleton |
| The shape of a file | `dotnet_outline` (members and lines, no bodies) |
| Who uses X? | `dotnet_find_references`: compiler-accurate, every target framework, plus WebForms, .asmx/.svc and XAML usages that grep and the compiler both miss |
| Who calls X, what X calls | `dotnet_call_hierarchy` (depth 1–3) |
| Base types, implementations, overrides | `dotnet_hierarchy` |

Pass symbols as a name, a dotted name (`Type.Member`), an id from an earlier result (`M:Ns.Type.Method(System.Int32)`)
or a position (`path/File.cs:42`). An ambiguous name returns candidates with their ids: call again with one of them.
External symbols (packages, the framework) name their package and version, and `include='source'` decompiles them.

Before deleting a member that looks unused, check `dotnet_find_references`: WebForms markup (`OnClick="..."`),
.asmx/.svc files and XAML event handlers reference code the compiler never sees.

## Edit, then verify

1. After editing .cs/.vb files, the plugin's hook runs `dotnet_check` and puts any new compiler error in front of you.
   Fix those before moving on. Call `dotnet_check` yourself after edits made outside the editing tools, or with
   `includeExisting=true` to see what was already broken.
2. `dotnet_code_actions` at a line offers the compiler's fixes and refactorings (add a missing `using`/`Imports`,
   implement an interface, generate a member…) as a diff; `apply=true` writes it and returns a fresh check.
3. Before you finish, `dotnet_test` with `affectedBy='changes'` runs only the tests that reach your edits, and reports
   failures with the assertion message and the stack frames in user code.
4. `dotnet_build` is the slow, complete check (analyzer warnings included). It picks Visual Studio's MSBuild for
   classic .NET Framework projects, where `dotnet build` fails.

`dotnet_check` reports only what changed since the workspace loaded, so pre-existing warnings never drown your errors.

## NuGet packages

- `dotnet_packages`: the versions restore resolved (direct and transitive, with the chain that brings each in),
  flagged when vulnerable, deprecated or outdated. `include='vulnerable'` or `'outdated'` filters.
- `dotnet_package_api` before calling a package API you are not certain about: signatures and docs of the exact
  version, read from the package's assemblies. APIs change between versions; do not guess them.

## .NET Framework modernization

1. `dotnet_legacy_inventory`: what is legacy and where (classic projects, packages.config, WebForms, ASMX, WCF,
   WinForms, WPF, BinaryFormatter, System.Configuration…), with counts and the files where it concentrates.
2. `dotnet_portability` (default target `net10.0`): the .NET Framework APIs that are missing or obsolete on the target,
   by technology, with locations and replacements. WinForms and WPF are checked against `-windows`, where they run.
3. `dotnet_config_inspect` on web.config/app.config: settings and connection strings with secrets masked, and the
   appsettings.json mapping. Never copy masked values back as real ones.
4. `dotnet_migration_plan`: steps bottom-up by project dependency, each with an approach, a size and file:line tasks.
   `write=true` saves `docs/modernization/assessment.md`, `plan.md` and `tasks.md` (ticked tasks survive regeneration).

The MCP prompts `explore`, `verify_changes`, `upgrade_package` and `modernize` chain these tools for the common cases.

## Limits

- Classic .NET Framework projects need Windows with Visual Studio 2022+ or Build Tools for full fidelity;
  `dotnet_workspace` says when they load with less.
- A server started with `--read-only` refuses to write files, build and run tests; with `--offline` the package tools
  use only the local NuGet caches. The tools say so when they refuse.
- Farol reads and writes only inside the directory it was started in (and `Farol:TrustedPaths`); paths outside are
  refused with an explanation.

# Farol

> A lighthouse for AI agents in .NET codebases, from .NET Framework to .NET 11.

Farol is an open-source [Model Context Protocol](https://modelcontextprotocol.io) server that gives AI coding agents (Claude Code, GitHub Copilot, Cursor, Codex) compiler-accurate understanding of C# and VB.NET solutions, including the legacy ones other tools skip: classic `.csproj`/`.vbproj`, `packages.config`, WebForms, WCF, ASMX, WinForms and WPF.

**Status:** pre-alpha. Phase 1 is in progress: workspace, navigation, markup references, edit verification, build and test, and packages are done. Scope: [spec 001](docs/specs/001-mvp.md) · progress: [Phase 1 plan](docs/plans/phase-1.md) · spike results: [Phase 0](docs/spikes/phase-0-results.md).

## Tools

| Tool | What it does |
|---|---|
| `dotnet_overview` | One-call map of a solution: languages, SDK-style vs classic projects, target frameworks, app models, test frameworks and the build command that works. |
| `dotnet_workspace` | Loads a solution and reports load state, failed projects and the MSBuild/Visual Studio toolchain in use. File and project edits are picked up automatically. |
| `dotnet_find_symbols` | Finds declarations by name: exact, prefix, substring or camel-case humps (`OrdCalc` → `OrderCalculator`). |
| `dotnet_symbol` | Signature, documentation and optionally the source of one symbol; a skeleton for types. External symbols name the package and version (or framework) they come from, and their source is decompiled from the implementation assembly. Ambiguous names return candidates. |
| `dotnet_find_references` | Compiler-accurate references across C#, VB and every target framework, classified as call, new, read, write…, plus markup references the compiler never sees (WebForms, .asmx/.svc/.ashx/.asax, XAML). |
| `dotnet_hierarchy` | Base types, interfaces, derived types, implementations and overrides. |
| `dotnet_call_hierarchy` | Callers or callees as a tree (depth 1–3) with call sites. |
| `dotnet_outline` | Types and members of a file or type with signatures and lines, no bodies. |
| `dotnet_check` | The errors and warnings your edits introduced since the workspace loaded, never the pre-existing ones, including in the projects that depend on the edited code (a C# signature change shows up as the VB error it causes). Edits inside member bodies re-check only the edited files, so it answers in about a second. Compiler diagnostics only. |
| `dotnet_code_actions` | Visual Studio's lightbulb for one line: compiler fixes (add a missing `using`/`Imports`, generate a member, implement an interface…) and refactorings (extract method…). Previews a unified diff without touching files; `apply=true` writes the change atomically and returns a fresh check. |
| `dotnet_build` | Builds with the toolchain that works: `dotnet build` for SDK-style projects, Visual Studio's MSBuild (restoring `packages.config`) for classic .NET Framework ones. Errors and warnings come from the binary log, deduplicated across target frameworks and grouped by project. |
| `dotnet_test` | Runs all tests, tests matching a name, or only those that reach a symbol or your edits (`affectedBy`). Reports failures only: the assertion message and the stack frames in your code. `vstest.console` for classic test projects, `dotnet test` (VSTest or Microsoft.Testing.Platform, per `global.json`) for SDK-style ones. |
| `dotnet_packages` | NuGet packages per project with the versions restore resolved: direct, and transitive with the chain that brings each one in. PackageReference (central package management included) and `packages.config`. Flags vulnerable packages (advisories), deprecated ones (with the replacement) and newer versions, from the solution's feeds. |
| `dotnet_package_api` | The public API of an exact package version: signatures and XML documentation summaries read from the package's assemblies, never guessed. Local NuGet caches first, then the solution's feeds. |

Symbols can be passed as names, dotted names, documentation comment IDs from earlier results, or `path:line`.

## Requirements

- .NET 10 SDK.
- Classic .NET Framework projects: Windows with Visual Studio 2022+ or Build Tools (full fidelity comes from Visual Studio's MSBuild); running their tests also needs Visual Studio's testing tools (`vstest.console.exe`).

## Build and test

```bash
dotnet build Farol.slnx
dotnet test --solution Farol.slnx
```

To work on Farol in an IDE, use one that supports .NET 10: Visual Studio 2026 (18.0+), VS Code with C# Dev Kit, or a recent Rider. **Visual Studio 2022 cannot open the solution:** the .NET 10 SDK requires MSBuild 18.0, and Visual Studio 2022 ships MSBuild 17.14. Farol can still analyze your solutions with Visual Studio 2022 installed; this only concerns building Farol itself.

## Use it from Claude Code

After building, add to the `.mcp.json` of the repository you want to analyze:

```json
{
  "mcpServers": {
    "farol": {
      "command": "dotnet",
      "args": ["C:/path/to/Farol/src/Farol.Host/bin/Debug/net10.0/Farol.Host.dll"]
    }
  }
}
```

Farol discovers the solution in the working directory. Pass `--workspace <path>` to pick one explicitly.

## Configuration

| Setting | Command line | Default | Meaning |
|---|---|---|---|
| `Farol:RootDirectory` | `--root <dir>` | working directory | Where the default workspace is discovered. Farol trusts this directory. |
| `Farol:DefaultWorkspace` | `--workspace <path>` | discovered | The solution or project used when a tool call names none. |
| `Farol:AutoLoad` | `--autoload false` | `true` | Start loading the default workspace at startup. |
| `Farol:ReadOnly` | `--read-only` | `false` | Refuse writing files, building and running tests; tools explain the refusal. |
| `Farol:Offline` | `--offline` | `false` | Never touch the network: `dotnet_packages` reports the vulnerabilities the last restore recorded, and `dotnet_package_api` reads only packages already in the local NuGet caches. |
| `Farol:TrustedPaths` | `--Farol:TrustedPaths:0 <dir>` | none | More directories whose solutions may be loaded and whose files may be read or written. |
| `Farol:BuildTimeoutMinutes` | `--Farol:BuildTimeoutMinutes 30` | `15` | A longer build is stopped with its whole process tree. |
| `Farol:TestTimeoutMinutes` | `--Farol:TestTimeoutMinutes 30` | `20` | The same, per test project run. |

Settings can also come from `appsettings.json` next to the executable and from environment variables such as `Farol__TrustedPaths__0`.

**Trust:** loading a solution runs its build logic (MSBuild evaluation, analyzers, source generators). Farol only loads solutions, reads paths and writes files inside the root it was started in and `Farol:TrustedPaths`, after resolving `..` and symbolic links; anything else is refused with an error that says how to allow it.

**NuGet:** the package tools use the NuGet configuration restore uses (the `nuget.config` files from the solution up, package source mapping, `auditSources`), the HTTP cache, and credential providers such as Azure Artifacts' in non-interactive mode: if a private feed needs a sign-in, run `dotnet restore --interactive` once. A package `dotnet_package_api` downloads goes into the global packages folder, as restore would put it.

## Use it from VS Code

Not yet validated for the MVP (Claude Code and Visual Studio 2026 are). Add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "farol": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["C:/path/to/Farol/src/Farol.Host/bin/Debug/net10.0/Farol.Host.dll"]
    }
  }
}
```

## Architecture

```
Farol.Host    stdio today, HTTP later · configuration · server instructions
  └─ Farol.Tools    one vertical slice per tool, grouped by toolset
       └─ Farol.Engine    Roslyn/MSBuild workspaces, discovery, analysis
            └─ Farol.Core    no Roslyn: token budgets, paths, errors, process execution
```

Tools always take the workspace as an explicit parameter (mirrored into the `Mcp-Param-Workspace` header), return edits as diffs and paths relative to the workspace root, so the same code can later run behind a self-hosted gateway.

## License

MIT

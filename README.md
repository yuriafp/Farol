# Farol

> A lighthouse for AI agents in .NET codebases, from .NET Framework to .NET 11.

Farol is an open-source [Model Context Protocol](https://modelcontextprotocol.io) server that gives AI coding agents (Claude Code, GitHub Copilot, Cursor, Codex) compiler-accurate understanding of C# and VB.NET solutions, including the legacy ones other tools skip: classic `.csproj`/`.vbproj`, `packages.config`, WebForms, WCF, ASMX, WinForms and WPF.

**Status:** alpha, [`Farol.Mcp` 0.1.0-alpha.1](https://www.nuget.org/packages/Farol.Mcp/0.1.0-alpha.1) on NuGet. Phase 1 is in progress: all 18 tools are done (workspace, navigation, markup references, edit verification, build and test, packages, legacy modernization), and so are the Claude Code plugin and the performance benchmarks, which meet the spec's targets. The eval suite meets the first exit criterion with Claude Haiku 4.5 on a harder task set, 59% success against 28% for grep + build with 32% fewer tokens ([results](evals/results/2026-10-07-haiku.md)); with Sonnet 5.5, grep + build had already solved every task of the first set ([results](evals/results/2026-10-05.md)). Scope: [spec 001](docs/specs/001-mvp.md) · progress: [Phase 1 plan](docs/plans/phase-1.md) · spike results: [Phase 0](docs/spikes/phase-0-results.md).

## Tools

| Tool | What it does |
|---|---|
| `dotnet_overview` | One-call map of a solution: languages, SDK-style vs classic projects, target frameworks, app models, test frameworks and the build command that works. |
| `dotnet_workspace` | Loads a solution and reports load state, failed projects and the MSBuild/Visual Studio toolchain in use, and whether the background warm-up that makes searches fast is done. File and project edits are picked up automatically. |
| `dotnet_find_symbols` | Finds declarations by name: exact, prefix, substring or camel-case humps (`OrdCalc` → `OrderCalculator`). |
| `dotnet_symbol` | Signature, documentation and optionally the source of one symbol; a skeleton for types. External symbols name the package and version (or framework) they come from, and their source is decompiled from the implementation assembly. Ambiguous names return candidates. |
| `dotnet_find_references` | Compiler-accurate references across C#, VB and every target framework, classified as call, new, read, write… (a type's include where it is created), plus markup references the compiler never sees (WebForms, .asmx/.svc/.ashx/.asax, XAML). |
| `dotnet_hierarchy` | Base types, interfaces, derived types, implementations and overrides. |
| `dotnet_call_hierarchy` | Callers or callees as a tree (depth 1–3) with call sites. |
| `dotnet_outline` | Types and members of a file or type with signatures and lines, no bodies. |
| `dotnet_check` | The errors and warnings your edits introduced since the workspace loaded, never the pre-existing ones, including in the projects that depend on the edited code (a C# signature change shows up as the VB error it causes). Edits inside member bodies re-check only the edited files, so it answers in about a second. Compiler diagnostics only. |
| `dotnet_code_actions` | Visual Studio's lightbulb for one line: compiler fixes (add a missing `using`/`Imports`, generate a member, implement an interface…) and refactorings (extract method…). Previews a unified diff without touching files; `apply=true` writes the change atomically and returns a fresh check. |
| `dotnet_build` | Builds with the toolchain that works: `dotnet build` for SDK-style projects, Visual Studio's MSBuild (restoring `packages.config`) for classic .NET Framework ones. Errors and warnings come from the binary log, deduplicated across target frameworks and grouped by project. |
| `dotnet_test` | Runs all tests, tests matching a name, or only those that reach a symbol or your edits (`affectedBy`). Reports failures only: the assertion message and the stack frames in your code. `vstest.console` for classic test projects, `dotnet test` (VSTest or Microsoft.Testing.Platform, per `global.json`) for SDK-style ones. |
| `dotnet_packages` | NuGet packages per project with the versions restore resolved: direct, and transitive with the chain that brings each one in. PackageReference (central package management included) and `packages.config`. Flags vulnerable packages (advisories), deprecated ones (with the replacement) and newer versions, from the solution's feeds. |
| `dotnet_package_api` | The public API of an exact package version: signatures and XML documentation summaries read from the package's assemblies, never guessed. Local NuGet caches first, then the solution's feeds. |
| `dotnet_legacy_inventory` | What is legacy and where, with counts: classic projects, .NET Framework targets, packages.config; WebForms pages, ASMX and WCF services, WinForms forms, WPF XAML; config settings and binding redirects; code uses of BinaryFormatter, System.Configuration, System.Web… with their lines and the files where they concentrate. |
| `dotnet_portability` | Every .NET Framework API the code uses, looked up in the target framework's reference assemblies: the missing and obsolete ones, grouped by technology, with their locations and the replacement. WinForms/WPF projects are checked against `-windows`. |
| `dotnet_config_inspect` | A web.config or app.config with secrets masked — app settings, connection strings, WCF services, system.web, binding redirects — and its appsettings.json mapping. |
| `dotnet_migration_plan` | An ordered plan, bottom-up by project dependency: one step per project with its approach (multi-target, retarget to `-windows`, or rebuild on ASP.NET Core), a size and tasks citing file:line. `write=true` saves `assessment.md`, `plan.md` and `tasks.md` to `docs/modernization/`; it never writes code. |

Symbols can be passed as names, dotted names, documentation comment IDs from earlier results, or `path:line`.

## Requirements

- .NET 10 SDK (it provides `dnx`, which runs Farol from NuGet without installing it).
- Classic .NET Framework projects: Windows with Visual Studio 2022+ or Build Tools (full fidelity comes from Visual Studio's MSBuild); running their tests also needs Visual Studio's testing tools (`vstest.console.exe`).

## Install

Farol ships on NuGet as [`Farol.Mcp`](https://www.nuget.org/packages/Farol.Mcp), an MCP server package that `dnx` runs on demand. It discovers the solution in the client's working directory; pass `--workspace <path>` to pick one.

### Claude Code

Install the plugin, which runs the server and adds a hook that checks every `.cs`/`.vb` edit and puts new compiler errors in front of Claude, plus a skill with the steps of a .NET Framework modernization:

```
/plugin marketplace add yuriafp/Farol
/plugin install farol@farol
```

The server's prompts appear as slash commands: `explore`, `verify_changes`, `upgrade_package` and `modernize`.

To add only the server, without the skill and the hook, put this in the `.mcp.json` of the repository:

```json
{
  "mcpServers": {
    "farol": {
      "command": "dotnet",
      "args": ["dnx", "Farol.Mcp@0.1.0-alpha.1", "--yes"]
    }
  }
}
```

### Visual Studio 2026

Add the server to a `.mcp.json` next to the solution (or to `%USERPROFILE%\.mcp.json` for every solution), then use it from GitHub Copilot Chat in agent mode:

```json
{
  "servers": {
    "farol": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["dnx", "Farol.Mcp@0.1.0-alpha.1", "--yes"]
    }
  }
}
```

Agents that keep their own MCP configuration ignore `.mcp.json`: add Farol to theirs, in the `mcpServers` form shown for Claude Code. Google Antigravity, for example, reads `%USERPROFILE%\.gemini\config\mcp_config.json`.

### VS Code

Not yet validated for the MVP (Claude Code and Visual Studio 2026 are). The same configuration goes in `.vscode/mcp.json`.

### Options

Farol's own options go after `--`, so `dnx` doesn't read them as its own: `"args": ["dnx", "Farol.Mcp@0.1.0-alpha.1", "--yes", "--", "--read-only"]`. The [Configuration](#configuration) table lists them.

### As a .NET tool

To install Farol once instead of running it through `dnx`, use `dotnet tool install --global Farol.Mcp --prerelease` (and `dotnet tool update` with the same arguments later). The client's command is then `farol`, and Farol's options are its arguments, without the `--`: `"args": ["--read-only"]`.

### From source

To run a local build instead, point the client at the host assembly:

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

**Trust:** loading a solution runs its build logic (MSBuild evaluation, analyzers, source generators), including the code generators a design-time build runs, as Visual Studio does when it opens the solution: one that writes into the source tree, such as a gRPC client with its `OutputDir` there, rewrites those files. Farol only loads solutions, reads paths and writes files inside the root it was started in and `Farol:TrustedPaths`, after resolving `..` and symbolic links; anything else is refused with an error that says how to allow it.

**NuGet:** the package tools use the NuGet configuration restore uses (the `nuget.config` files from the solution up, package source mapping, `auditSources`), the HTTP cache, and credential providers such as Azure Artifacts' in non-interactive mode: if a private feed needs a sign-in, run `dotnet restore --interactive` once. A package `dotnet_package_api` downloads goes into the global packages folder, as restore would put it.

## Build and test

```bash
dotnet build Farol.slnx
dotnet test --solution Farol.slnx
```

To work on Farol in an IDE, use one that supports .NET 10: Visual Studio 2026 (18.0+), VS Code with C# Dev Kit, or a recent Rider. **Visual Studio 2022 cannot open the solution:** the .NET 10 SDK requires MSBuild 18.0, and Visual Studio 2022 ships MSBuild 17.14. Farol can still analyze your solutions with Visual Studio 2022 installed; this only concerns building Farol itself.

CI builds and tests every push on Windows with Visual Studio 2026. Performance is measured against the spec's targets on Umbraco CMS and DNN Platform: [benchmarks/README.md](benchmarks/README.md). Whether Farol makes Claude Code better at .NET work is measured by an eval suite of 30 tasks run with and without it: [evals/README.md](evals/README.md).

Releasing: [docs/release.md](docs/release.md). Manual smoke tests in Claude Code and Visual Studio 2026: [docs/smoke-tests.md](docs/smoke-tests.md).

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

# Farol

> A lighthouse for AI agents in .NET codebases, from .NET Framework to .NET 11.

Farol is an open-source [Model Context Protocol](https://modelcontextprotocol.io) server that gives AI coding agents (Claude Code, GitHub Copilot, Cursor, Codex) compiler-accurate understanding of C# and VB.NET solutions, including the legacy ones other tools skip: classic `.csproj`/`.vbproj`, `packages.config`, WebForms, WCF, ASMX, WinForms and WPF.

**Status:** pre-alpha (Phase 0 done). Scope: [docs/specs/001-mvp.md](docs/specs/001-mvp.md) · spike results: [docs/spikes/phase-0-results.md](docs/spikes/phase-0-results.md).

## Tools

| Tool | What it does |
|---|---|
| `dotnet_overview` | One-call map of a solution: languages, SDK-style vs classic projects, target frameworks, app models, test frameworks and the build command that works. |
| `dotnet_workspace` | Loads a solution and reports load state, failed projects and the MSBuild/Visual Studio toolchain in use. File and project edits are picked up automatically. |
| `dotnet_find_symbols` | Finds declarations by name: exact, prefix, substring or camel-case humps (`OrdCalc` → `OrderCalculator`). |
| `dotnet_symbol` | Signature, documentation and optionally the source of one symbol; a skeleton for types. Ambiguous names return candidates. |
| `dotnet_find_references` | Compiler-accurate references across C#, VB and every target framework, classified as call, new, read, write…, plus markup references the compiler never sees (WebForms, .asmx/.svc/.ashx/.asax, XAML). |
| `dotnet_hierarchy` | Base types, interfaces, derived types, implementations and overrides. |
| `dotnet_call_hierarchy` | Callers or callees as a tree (depth 1–3) with call sites. |
| `dotnet_outline` | Types and members of a file or type with signatures and lines, no bodies. |

Symbols can be passed as names, dotted names, documentation comment IDs from earlier results, or `path:line`.

## Requirements

- .NET 10 SDK.
- Classic .NET Framework projects: Windows with Visual Studio 2022+ or Build Tools (full fidelity comes from Visual Studio's MSBuild).

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

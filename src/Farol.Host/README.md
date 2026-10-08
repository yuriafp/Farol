# Farol.Mcp

Farol is an open-source [Model Context Protocol](https://modelcontextprotocol.io) server that gives AI coding agents (Claude Code, GitHub Copilot, Cursor, Codex) compiler-accurate understanding of C# and VB.NET solutions, including legacy .NET Framework ones: classic `.csproj`/`.vbproj`, `packages.config`, WebForms, WCF, ASMX, WinForms and WPF.

**Status:** alpha.

## Run it

With the .NET 10 SDK installed, any MCP client can start Farol from NuGet with `dnx`; it discovers the solution in the client's working directory:

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

Claude Code users can install the Farol plugin instead (server, usage skill and a hook that checks every C#/VB edit): `/plugin marketplace add yuriafp/Farol`, then `/plugin install farol@farol`.

Classic .NET Framework projects need Windows with Visual Studio 2022+ or Build Tools for full fidelity.

## What it does

- **Workspace:** `dotnet_overview`, `dotnet_workspace`: a one-call map of the solution and the build command that works.
- **Navigation:** `dotnet_find_symbols`, `dotnet_symbol`, `dotnet_find_references`, `dotnet_hierarchy`, `dotnet_call_hierarchy`, `dotnet_outline`, across C#, VB and every target framework, including WebForms, .asmx/.svc and XAML references.
- **Verify:** `dotnet_check` (only the errors your edits introduced, in about a second) and `dotnet_code_actions` (the compiler's fixes and refactorings, as diffs).
- **Build and test:** `dotnet_build` (the toolchain that works, errors from the binary log) and `dotnet_test` (only the tests your edits reach).
- **Packages:** `dotnet_packages` (resolved versions, vulnerable and deprecated packages) and `dotnet_package_api` (the exact API of a package version).
- **Modernization:** `dotnet_legacy_inventory`, `dotnet_portability`, `dotnet_config_inspect` and `dotnet_migration_plan`.

Options, which go after `--` in the `dnx` arguments: `--read-only` refuses writing files, building and running tests; `--offline` never touches the network; `--workspace <path>` picks the solution; `--help` lists them all.

Documentation, source and issues: [github.com/yuriafp/Farol](https://github.com/yuriafp/Farol). License: MIT.

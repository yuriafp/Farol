# Changelog

All notable changes to Farol are recorded in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [0.1.0-alpha.1] - 2026-10-08

The first alpha.

### Added

- `Farol.Mcp` on nuget.org: an MCP server for C# and VB solutions, from classic .NET Framework projects to .NET 10, run
  with `dotnet dnx` or installed as a .NET tool.
- 18 tools:
  - workspace: `dotnet_workspace`, `dotnet_overview`;
  - navigation: `dotnet_find_symbols`, `dotnet_symbol`, `dotnet_find_references` (WebForms, ASMX/WCF and XAML markup
    included), `dotnet_hierarchy`, `dotnet_call_hierarchy`, `dotnet_outline`;
  - verification: `dotnet_check`, which reports only the diagnostics your edits introduced, and `dotnet_code_actions`;
  - build and test: `dotnet_build`, and `dotnet_test`, which can run only the tests your edits reach;
  - packages: `dotnet_packages`, `dotnet_package_api`;
  - legacy modernization: `dotnet_legacy_inventory`, `dotnet_portability`, `dotnet_config_inspect`,
    `dotnet_migration_plan`.
- Four MCP prompts: `explore`, `verify_changes`, `upgrade_package` and `modernize`.
- `--read-only` and `--offline` modes.
- A Claude Code plugin: the server, a hook that checks every `.cs` and `.vb` edit, and a .NET Framework modernization
  skill.

[0.1.0-alpha.1]: https://github.com/yuriafp/Farol/releases/tag/v0.1.0-alpha.1

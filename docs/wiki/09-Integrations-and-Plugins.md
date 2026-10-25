# Integrations & Plugins

Farol runs in any MCP client. This page covers Claude Code and Visual Studio 2026 (validated for the MVP), Google Antigravity, GitHub Copilot and VS Code.

---

## 1. Claude Code Plugin (`plugins/farol`)

The Claude Code plugin provides the richest integration with Farol by pairing the MCP server with an automated post-edit verification hook and a dedicated modernization skill.

```
plugins/farol/
├── .claude-plugin/
│   └── plugin.json       # Plugin manifest & marketplace metadata
├── .mcp.json             # MCP server declaration pinning Farol.Mcp
├── hooks/
│   └── hooks.json        # PostToolUse verification hook for C# and VB.NET
└── skills/farol/
    └── SKILL.md          # .NET Framework modernization playbook
```

### Installation
Add the repository marketplace and install the plugin directly from within Claude Code:

```bash
/plugin marketplace add yuriafp/Farol
/plugin install farol@farol
```

### The `PostToolUse` Verification Hook
Whenever Claude Code edits a C# or VB.NET file using `Edit`, `Write`, or `MultiEdit`, the hook automatically intercepts the action and triggers Farol's `dotnet_check`:

```json
{
  "type": "mcp_tool",
  "if": "Edit(**/*.cs)",
  "server": "plugin:farol:farol",
  "tool": "dotnet_check",
  "input": {
    "format": "hook",
    "edited": "${tool_input.file_path}"
  },
  "timeout": 60,
  "statusMessage": "Farol: checking the edit for new compiler errors"
}
```

A second entry does the same for `Edit(**/*.vb)`.

- **Immediate Interception**: The `edited` parameter forces Farol to read the edited file directly from disk, eliminating file watcher races.
- **Decision Blocking**: If the edit introduced new compiler errors, Farol returns `{"decision": "block", "reason": "..."}`, immediately alerting the agent to fix the issue before proceeding.
- **Warning Injection**: If new compiler warnings are introduced, Farol injects them into the agent's context as `additionalContext`.

### The Modernization Skill (`SKILL.md`)
Claude Code automatically activates the `farol` skill when tasked with modernizing legacy .NET Framework applications. It instructs the agent to execute tools in the proper sequence:
1. `dotnet_legacy_inventory` to map legacy technologies and hotspots.
2. `dotnet_portability` to evaluate missing and obsolete APIs against `net10.0`.
3. `dotnet_config_inspect` to review `web.config` settings with masked secrets.
4. `dotnet_migration_plan` to generate bottom-up architectural tasks.

---

## 2. Visual Studio 2026 & GitHub Copilot Chat

Visual Studio 2026 (version 18.0+) supports MCP servers natively.

### Configuration
Add Farol to `.mcp.json` next to your solution (or globally in `%USERPROFILE%\.mcp.json`):

```json
{
  "servers": {
    "farol": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["dnx", "Farol.Mcp@0.1.0-alpha.4", "--yes"]
    }
  }
}
```

Once configured, GitHub Copilot Chat in Agent Mode gets Farol's 18 tools.

---

## 3. Google Antigravity

Google Antigravity reads MCP configuration from `%USERPROFILE%\.gemini\config\mcp_config.json`.

Because Antigravity may launch MCP servers from its own installation directory rather than the active repository directory, configure Farol with a trusted path covering your source folder:

```json
{
  "mcpServers": {
    "farol": {
      "command": "dotnet",
      "args": ["dnx", "Farol.Mcp@0.1.0-alpha.4", "--yes", "--", "--Farol:TrustedPaths:0", "C:/Users/<you>/source/repos"]
    }
  }
}
```

Trust the folder that holds your repositories rather than making it the root: a solution in the folder Farol starts in still loads on its own, and any solution under the trusted folder loads once a call names it. Farol's own options go after `--`, so `dnx` doesn't read them as its own. *Tip: with Farol installed as a global .NET tool (`dotnet tool install --global Farol.Mcp --prerelease`), the command is `farol` and the args are just the options.*

---

## 4. Visual Studio Code

Not yet validated for the MVP (Claude Code and Visual Studio 2026 are); the configuration is the same as for Visual Studio.

Configure Farol in `.vscode/mcp.json` within your workspace:

```json
{
  "servers": {
    "farol": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["dnx", "Farol.Mcp@0.1.0-alpha.4", "--yes"]
    }
  }
}
```

---

## 5. NuGet Package Distribution (`Farol.Mcp`)

Farol is packaged as a framework-dependent .NET tool and published to nuget.org:
- **Package ID**: [`Farol.Mcp`](https://www.nuget.org/packages/Farol.Mcp)
- **Package Types**: `DotnetTool` and `McpServer`
- **Registry Metadata**: Ships with `.mcp/server.json` conforming to the Model Context Protocol Registry schema.
- **Embedded BuildHosts**: Ships with the required Roslyn MSBuild `BuildHost` binaries embedded in the package, enabling classic .NET Framework analysis even when run out of NuGet caches.

### Execution Options

#### On-Demand Execution via `dnx` (Recommended)
Requires .NET 10 SDK:
```bash
dotnet dnx Farol.Mcp@0.1.0-alpha.4 --yes
```
Pass Farol-specific arguments after `--`:
```bash
dotnet dnx Farol.Mcp@0.1.0-alpha.4 --yes -- --read-only --offline
```

#### Global .NET Tool Installation
```bash
# Install
dotnet tool install --global Farol.Mcp --prerelease

# Update
dotnet tool update --global Farol.Mcp --prerelease

# Run
farol --read-only
```

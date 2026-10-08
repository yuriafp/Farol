# Manual smoke tests

AC-36 of [spec 001](specs/001-mvp.md): before each release, a person runs these checks in Claude Code and in Visual Studio 2026 on a Windows machine with Visual Studio installed, and records the result at the end of this file. The automated suite covers the tools; these checks cover what only a real client shows: installation, discovery, the hook, prompts and agent behavior.

Use the repository's fixtures as the solutions under test: `tests/fixtures/legacy` (classic .NET Framework) and `tests/fixtures/modern` (.NET 10). Work on a copy, since some steps edit files.

## Before the package is published

The setups below run the published package from nuget.org. To check a release candidate instead, pack it first
(`dotnet pack src/Farol.Host -c Release -o artifacts/packages`, as in [release.md](release.md)) and point the clients
at that folder with a throwaway NuGet cache. The candidate has the release's version, so in your real cache it would
later shadow the published package.

- **Claude Code:** instead of installing the plugin from the marketplace, copy `plugins/farol` to a folder outside the
  fixture copy and replace the copy's `.mcp.json` with the one below. Then start Claude Code in the fixture copy with
  `claude --plugin-dir <the plugin copy>`. The skill, the hook and the prompts are the plugin as it will ship.
- **Visual Studio 2026:** put the same `command`, `args` and `env` in the `.mcp.json` next to `Legacy.sln`, under
  `servers`.
- **An agent that runs the .NET tool** (`"command": "farol"`): run
  `dotnet tool update --global Farol.Mcp --version <version> --add-source <repository>/artifacts/packages` (`install`
  the first time). The tool keeps its package in its own store, not in the NuGet cache.

```json
{
  "mcpServers": {
    "farol": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["dnx", "Farol.Mcp@<version>", "--yes", "--add-source", "<repository>/artifacts/packages"],
      "env": { "NUGET_PACKAGES": "<a new, empty folder>" }
    }
  }
}
```

For C9, append `"--", "--read-only"` to `args`. Delete the throwaway cache folder when you are done.

## Claude Code

Setup: Claude Code with the plugin installed (`/plugin marketplace add yuriafp/Farol`, then `/plugin install farol@farol`), started in a copy of `tests/fixtures/legacy`.

| # | Do | Expect |
|---|---|---|
| C1 | `/mcp` | The `farol` server (from the plugin) is connected, with 18 tools. |
| C2 | Ask: "Give me an overview of this solution." | Claude calls `dotnet_overview`; the answer names 6 classic projects, WebForms, ASMX, WCF, WinForms, WPF and the VB project. |
| C3 | Ask: "Who calls OrderCalculator.GetTotal?" | Claude uses `dotnet_find_references` or `dotnet_call_hierarchy`, not grep; callers in C# and VB, with path:line. |
| C4 | Ask Claude to change `return order.Subtotal() * (1 + TaxRate());` in `Legacy.Core/Orders/OrderCalculator.cs` to return a string. | Right after the edit, the hook's status line shows "Farol: checking the edit…" and Claude receives the new error (CS0029) and fixes it or reports it. |
| C5 | Ask Claude to add an unused variable to a .cs file. | The hook adds the new warning (CS0219) as context, without blocking. |
| C6 | Edit a non-code file (for example `Legacy.sln` comments) through Claude. | No hook output. |
| C7 | Run the `modernize` prompt from the slash-command menu. | Claude runs inventory, portability, config inspection and the plan; it changes no file. |
| C8 | Ask: "What is the API of Newtonsoft.Json 12.0.3's JsonConvert?" | Claude calls `dotnet_package_api` and quotes signatures from that version. |
| C9 | Start Claude Code with the server in read-only mode (`.mcp.json` with `"--", "--read-only"`) and ask for a build with `dotnet_build`. | `dotnet_build` is refused with an explanation. (`--read-only` covers Farol's tools, not the agent's own shell: asked only to "build the solution", the agent may run MSBuild itself.) |

## Visual Studio 2026

Setup: a `.mcp.json` next to `Legacy.sln` in the copy, as in the README, then open the solution. Use an agent that supports MCP servers in Visual Studio, such as GitHub Copilot Chat in agent mode, and record which one in the results. An agent that keeps its own MCP configuration, such as Google Antigravity, ignores `.mcp.json`: configure Farol there, as the README says.

| # | Do | Expect |
|---|---|---|
| V1 | Open the agent's list of tools (the tools picker in Copilot Chat). | `farol` is listed with its 18 tools. |
| V2 | Ask: "Use dotnet_overview to describe this solution." | The tool runs (after a permission prompt) and the answer matches C2. |
| V3 | Ask: "Find the references to OrderCalculator.GetTotal with Farol." | Results in C# and VB with path:line, matching C3. |
| V4 | Edit `OrderCalculator.cs` to introduce a type error, then ask: "Run dotnet_check." | The new error is reported with its path:line, and no pre-existing diagnostic. |
| V5 | Ask for the migration plan to net10.0. | `dotnet_migration_plan` returns steps starting with Legacy.Core. |
| V6 | Open the prompts list (`/` in the chat box). | The `explore`, `verify_changes`, `upgrade_package` and `modernize` prompts appear. |

## Results

| Date | Version | Client and version | Result | Notes |
|---|---|---|---|---|
| 2026-10-08 | 0.1.0-alpha.1 | Claude Code 2.1.286, headless (`claude -p`), model `claude-haiku-5-5`, the plugin from a local copy (`--plugin-dir`), no other MCP server | C1–C9 pass | Run by Claude at the developer's request, so each UI check was verified through its headless equivalent. **C1:** the init message (`farol` connected, 18 tools, 4 prompts). **C4:** the debug log shows the hook ran `dotnet_check` and blocked with CS0029 right after the edit; Claude reported the errors. **C5:** CS0219 reached Claude as additional context, without blocking. **C6:** the hook's conditions skipped the `.sln` edit. **C7:** invoked as `/mcp__plugin_farol_farol__modernize`; no file changed. **C9:** asked only to build, the agent ran MSBuild through its shell; asked for `dotnet_build`, the tool refused and explained. The package was the release candidate, the same file published to nuget.org, where `dnx` from a clean cache also works. |
| 2026-10-08 | 0.1.0-alpha.1 | Visual Studio Professional 2026 18.11 (Insiders) with the Google Antigravity extension 1.0.261005.0, model Gemini 3.1 Pro (High); Farol installed from nuget.org as a global tool, no other MCP server | V1–V6 pass | Run by the developer. V1–V5 were verified in the transcript the agent saved, with every Farol call and its full response; V6 was checked by the developer in the client. **Setup:** Antigravity reads its own MCP configuration (`%USERPROFILE%\.gemini\config\mcp_config.json`), not the solution's `.mcp.json`, so at first the agent had no Farol; after `dotnet tool install --global Farol.Mcp`, the agent added `farol` there and removed the other server. **V1:** after the reload, the 18 tools. **V2:** 6 classic projects (5 C#, 1 VB), WinForms, WebForms, ASMX, WCF and WPF. **V3:** the 8 calls in C# and VB, with path:line. **V4:** the agent made the edit itself; `dotnet_check` reported only CS0029 at `OrderCalculator.cs:23`, not the pre-existing CS0168. **V5:** 6 steps, Legacy.Core first; no file written. The transcript does not show whether the client asked before each call. Before configuring it, the agent ran `farol --help`, which starts the server instead of printing usage. |

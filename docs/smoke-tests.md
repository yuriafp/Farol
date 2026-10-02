# Manual smoke tests

AC-36 of [spec 001](specs/001-mvp.md): before each release, a person runs these checks in Claude Code and in Visual Studio 2026 on a Windows machine with Visual Studio installed, and records the result at the end of this file. The automated suite covers the tools; these checks cover what only a real client shows: installation, discovery, the hook, prompts and agent behavior.

Use the repository's fixtures as the solutions under test: `tests/fixtures/legacy` (classic .NET Framework) and `tests/fixtures/modern` (.NET 10). Work on a copy, since some steps edit files.

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
| C9 | Start Claude Code with the server in read-only mode (`.mcp.json` with `"--", "--read-only"`) and ask for a build. | `dotnet_build` is refused with an explanation. |

## Visual Studio 2026

Setup: a `.mcp.json` next to `Legacy.sln` in the copy, as in the README, then open the solution. Use GitHub Copilot Chat in agent mode.

| # | Do | Expect |
|---|---|---|
| V1 | Open the tools picker in Copilot Chat. | `farol` is listed with its 18 tools. |
| V2 | Ask: "Use dotnet_overview to describe this solution." | The tool runs (after a permission prompt) and the answer matches C2. |
| V3 | Ask: "Find the references to OrderCalculator.GetTotal with Farol." | Results in C# and VB with path:line, matching C3. |
| V4 | Edit `OrderCalculator.cs` to introduce a type error, then ask: "Run dotnet_check." | The new error is reported with its path:line, and no pre-existing diagnostic. |
| V5 | Ask for the migration plan to net10.0. | `dotnet_migration_plan` returns steps starting with Legacy.Core. |
| V6 | Open the prompts list (`/` in the chat box). | The `explore`, `verify_changes`, `upgrade_package` and `modernize` prompts appear. |

## Results

| Date | Version | Client and version | Result | Notes |
|---|---|---|---|---|
| | | | | |

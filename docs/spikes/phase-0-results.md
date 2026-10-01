# Phase 0 — spike results

**Date:** 2026-09-30
**Environment:** Windows 11, .NET SDK 10.0.401, Visual Studio Enterprise 2022 17.14, Roslyn 5.9.0, ModelContextProtocol 2.2.0.
**Fixtures:** `tests/fixtures/legacy` (5 classic .NET Framework 4.8 projects: C#, VB.NET, WebForms + ASMX + WCF, WinForms, WPF) and `tests/fixtures/modern` (.NET 10, `.slnx`, central package management, multi-target `net10.0;net48`, ASP.NET Core).
**Reproduce:** `dotnet test --solution Farol.slnx` writes the raw reports to `artifacts/spikes/`.

## Spike A — loading legacy solutions

### Path 1: MSBuildWorkspace + BuildHost-net472 (design-time load)

| project | language | documents | compiler errors |
|---|---|---|---|
| Legacy.Core | C# | 7 | 0 |
| Legacy.Desktop (WinForms) | C# | 4 | 0 |
| Legacy.VbLib | VB | 3 | 0 |
| Legacy.Web (WebForms + ASMX + WCF) | C# | 7 | 0 |
| Legacy.Wpf | C# | 5 | 0 |

- 5/5 projects loaded in **3.1 s**, with no load issues and **zero compiler errors**. No prior build needed.
- **WPF:** the feared false `InitializeComponent` errors did not happen. The design-time load runs the XAML compiler and generates `obj/Debug/*.g.cs`.
- **Side effect:** loading writes to `obj/` in the analyzed repository, as Visual Studio does.
- **Ground truth:** `MSBuild.exe Legacy.sln` (VS 17.14) builds all 5 projects with exit code 0, so "zero errors" matches reality.

### Path 2: binary-log replay (Basic.CompilerLog 0.9.63)

| project | language | errors (raw replay) | errors (with the VB /sdkpath fix) |
|---|---|---|---|
| Legacy.Core | C# | 0 | 0 |
| Legacy.Desktop | C# | 0 | 0 |
| Legacy.VbLib | VB | **165** | 0 |
| Legacy.Web | C# | 0 | 0 |
| Legacy.Wpf | C# | 0 | 0 |

- Needs a build that **actually compiles**: an up-to-date incremental build skips `CoreCompile` and the log holds no compiler calls, so we use `-t:Rebuild`. The rebuild took 2.0 s and the replay 1.6 s.
- **Finding:** classic VB projects get `mscorlib` and `Microsoft.VisualBasic` implicitly from `vbc`'s `/sdkpath`, while C# passes `mscorlib` as an explicit `/reference`. The replay does not reproduce that resolution, which produces 165 errors (BC30652, BC30002). Adding those two references from `/sdkpath` brings the errors to zero. Worth proposing upstream to Basic.CompilerLog (MIT).

### Decision

| | MSBuildWorkspace + BuildHost-net472 | Binary-log replay |
|---|---|---|
| Fidelity (fixtures) | 5/5 with no errors | 5/5 with no errors, only with the VB fix |
| Prerequisite | VS/Build Tools on Windows | a full build with `-bl` |
| Cost | 3.1 s, no build | rebuild + 1.6 s |
| Use | **MVP's primary loader** | Phase 2 fallback: CI/Linux, design-time failures, exact build parity |

## Spike B — MCP 2.x host over stdio

- The real executable (`Farol.Host`) was started as a child process by the SDK's stdio client (`StdioClientTransport`): `server/discover`, `tools/list` and `tools/call` worked end to end. This also confirms that no log reaches stdout, since any line there would break the JSON-RPC stream.
- `--root` and `--autoload` worked, as did the server `instructions` (visible to the client).
- Tool schemas publish `x-mcp-header: "Workspace"`, ready for a gateway to route by `Mcp-Param-Workspace`.
- **Pending:** manual validation inside Claude Code and Visual Studio 2026, the clients chosen in spec round 5 (requires approving the server in the client's UI).

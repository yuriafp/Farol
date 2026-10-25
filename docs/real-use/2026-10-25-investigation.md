# Why agents in Claude Code did not use Farol, 2026-10-25

A follow-up to the [real-use results](2026-10-23.md), which the developer asked for before 0.1.0-alpha.5: apart from
the plugin's hook, agents in Claude Code made 2 calls to Farol in ten days. Sources: the two Claude Code sessions of
the period, read from their transcripts on the developer's machine (a game repository, 10-13 to 10-23, and a fork of
an open-source game server, 10-16 to 10-25), and reproductions with 0.1.0-alpha.4 on a local clone of the fork.

## Findings

1. **The hook's failures never reached the agent.** Both sessions were opened in the folder that holds all of the
   developer's repositories, where the hook's `dotnet_check` found 37 solutions and failed with `workspace_ambiguous`.
   Claude Code records a failing `mcp_tool` hook as a non-blocking hook error, which the agent does not see: 210 of them
   in the game session and 31 in the fork session, and neither agent ever mentioned one. Opened in either repository,
   the hook still fails (2 and 4 candidates; see the results, finding 1).
2. **A solution still loading sent the agent back to grep.** In the fork session the agent decided to navigate the C#
   code with Farol and loaded its tools. Its first call, `dotnet_workspace` with `action=status`, answered
   `state: loading`, with the hint to call again with `action='load'` to wait. It did not call again, and navigated
   with shell commands, `Grep` and `Read` for the rest of that task. Farol's other tools wait for a load to finish,
   about 15 s for this 55-project solution, but the answer did not say so.
3. **A preview SDK broke the load, and Farol did not say so.** The fork has no `global.json`, so it builds with the
   newest SDK installed: 11.0.100-rc.2, which came with Visual Studio 18.12 Insiders on 10-13. That SDK's Razor source
   generator references Microsoft.CodeAnalysis 5.12, and Farol runs 5.9, so the generator does not run inside Farol and
   the half of every Blazor component it generates is missing. On a clone of the fork, the load has 598 errors with SDK
   11 RC and none with SDK 10.0.401 pinned in `global.json`, and `dotnet build` succeeds with either. In the
   developer's checkout that day, `dotnet_check` with `includeExisting` listed 12,826 errors that existed at load, and
   the agent concluded that Farol had not resolved the package references and ran a real build. Farol's load issues
   named nothing. No Roslyn newer than 5.9 is on nuget.org yet; .NET 11 ships in November 2026, and from then on
   every solution that uses Razor or another SDK generator loads this way for developers on SDK 11.
4. **Farol locked the solution's own generators, and the build failed.** The fork's projects reference three generator
   projects of the same solution as analyzers (`OutputItemType="Analyzer"`), from their `bin\Debug` folders. Farol
   loads those assemblies into its process and holds the files, so the developer's Debug build could not replace them
   (MSB3026 and MSB3061: the file "is locked by Farol.Host"); the agent built in Release instead. Reproduced on the
   clone with a build of one generator while Farol held the workspace.
5. **The game session mostly wrote new code, and never loaded Farol's tools.** 137 `Write` and 296 `Edit` calls,
   against 144 `Read` and 13 `Grep`. Claude Code lists MCP tools by name only until the agent loads them, and that
   agent never did, so the hook was Farol's only part in the session, and it failed throughout (finding 1).

## What the findings ask of the next version

| Finding | Change | Status |
|---|---|---|
| 1 | A call with a file and no workspace uses the solution nearest above the file; when the hook cannot pick one, it tells the agent once | proposed |
| 2 | `dotnet_workspace` status, while a load runs, says that the other tools wait for it and how long the last load took | proposed |
| 3 | A generator or analyzer that needs a newer compiler than Farol's is a load issue, naming the projects that miss its code and the fix | proposed |
| 3 | Move to the Roslyn of .NET 11 when nuget.org has it | after .NET 11 ships |
| 4 | Analyzers and generators are loaded from a copy, so building while Farol runs succeeds | proposed |

# Changelog

All notable changes to Farol are recorded in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- An edit made while the workspace loads is no longer lost. The load could read the file already edited and take the
  edit into its baseline, so no later check reported it. A C# or VB file that Farol hears of before the load is done
  (the hook's `edited`, or a `path`), or whose time on disk is later than the load's start, is now kept out of the
  baseline: the next `dotnet_check` that covers it, once the load is done, lists all of its errors and warnings as new
  and says that Farol did not see the file before the change, so some may be older. The hook still does not wait for
  the load; during it, the hook tells the agent, as added context, that the edit is checked once the load is done.

## [0.1.0-alpha.5] - 2026-10-26

Changes from two weeks of real use ([results](docs/real-use/2026-10-23.md), [investigation](docs/real-use/2026-10-25-investigation.md)).

### Changed

- A call about a file (the hook's `edited`, a `path`, or a `path:line` symbol) without `workspace`, in a root that holds
  no single solution, uses the workspace in use whose folder holds the file, else the solution nearest above the file.
  Started in a folder of repositories, the plugin's edit hook now checks each edit against its own solution.
- When the hook still cannot pick a solution, its first answer in a server tells the agent how to name one, as added
  context: Claude Code never shows an agent a hook's error. Edits to files that are not C# or VB, or that are outside
  the trusted directories, are passed over before a solution is picked.
- `dotnet_workspace` status, while a load runs, says that the other tools wait for the load, and how long the last load
  of that workspace in the same server took. Told only to call again, an agent went back to grep.

### Fixed

- Farol no longer holds the files of analyzers and source generators, nor of the libraries they use: it loads copies
  of them, under the temp directory, so a solution that builds its own generators can rebuild them while Farol runs. A
  later server removes the copies once the process that made them has exited. Analyzers that cannot be copied, such as
  ones a build is writing, load from their own files, and a load issue says so.
- An analyzer or source generator built for a newer compiler than Farol runs no longer fails without a word. When it
  comes with the .NET SDK and no `global.json` pins the SDK, Farol takes it from the newest installed SDK whose
  compiler it runs, and a load issue says so: on a .NET 11 preview SDK, the Razor generator did not run, and Blazor
  solutions loaded with hundreds of errors that a build does not have. Otherwise a load issue names the analyzer, the
  compiler version it needs and the projects missing its code, and says how to fix it.

### Security

- `System.Security.Cryptography.Pkcs`, which NuGet's libraries bring in at 8.0.1, is raised to 10.0.13, which fixes two
  denial-of-service advisories (GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7).

## [0.1.0-alpha.4] - 2026-10-13

### Changed

- A call without `workspace` uses the workspace already in use when the root holds no single solution, as when a
  client starts Farol in a folder of repositories: name the solution once, then leave it out.
- A relative path that names nothing under the root resolves from the workspace's folder, so paths written from the
  solution's folder work when the solution is not in the root. Paths still have to be inside the trusted directories.
- The tools that take a `symbol` find types from packages and the framework by name (`JsonConvert`,
  `System.Text.StringBuilder`), and their members by a name qualified with the type (`StringBuilder.Capacity`). Before,
  only the documentation comment ID found them. `dotnet_outline` of such a type is an `invalid_argument` error, since it
  has no source, and `dotnet_hierarchy` lists derived types, implementations and overrides from the workspace's code
  only, not the hundreds an interface such as `IDisposable` has in the framework.
- `dotnet_check` with a `path` reads that file first, as it does with `edited`, so a file created a moment ago is checked
  instead of refused. A file its project leaves out (`bin`, `obj`, `Compile Remove`) stays out.
- The descriptions of path parameters and the server instructions say paths are relative to the root, as responses
  write them, instead of to "the workspace root".

### Fixed

- The server no longer fails at startup when its root holds folders it may not list, as a drive or a user profile does:
  finding the workspace passes over them, and preloading it no longer delays the start.
- Editing a file its project leaves out (`Compile Remove`) no longer adds it to the workspace, and a new file still held
  by the program writing it is read again on the next call instead of dropped.
- A `workspace` folder with several solutions, or none, gets an error that writes them relative to the root, so they
  can be passed back as written, and no longer writes the folder's absolute path.
- An empty `query`, `symbol` or `path` is an `invalid_argument` error that says what to pass. Before, it was an error
  with no code or hint.
- `dotnet_find_symbols` with an unknown `project` lists the projects. Before, it answered that no declarations match.
- A path the workspace does not compile is named as given (`dotnet_check` named only the file), and the error says
  whether there is no such file, it is a folder, it is not C# or VB, or its project leaves it out, without writing an
  absolute path.
- A `path:line` position on a drive whose letter starts a documentation comment ID, such as `F:\src\A.cs:12`, is read as
  a position.
- `dotnet_config_inspect` with a relative path and no workspace to resolve it from reports why there is none, instead of
  a missing file.
- Two Farol processes building or testing the same workspace, as two clients on one repository do, no longer fail with
  an unexpected error: the second waits for the first and says so in its progress. After the build and test timeouts
  it stops waiting with a `conflict` error.
- A call made while the workspace reloads no longer fails with "failed to load" and no reason: it waits for the reload.
- A path or `workspace` with a null character, or too long to be a path, is an `invalid_argument` error that quotes
  only its start. Before, it was an unexpected error, and the server's log echoed the whole argument.
- An argument of the wrong JSON type (an object where a string goes) or a missing required argument is an
  `invalid_argument` error that names it. Before, the answer was "An error occurred invoking" with no detail.

## [0.1.0-alpha.3] - 2026-10-09

### Added

- An opt-in usage log that stays on the machine: `--usage-log`, or `Farol__UsageLog=true` in the environment. It keeps
  one JSON line per session, workspace load and warm-up, and tool call, with durations, outcomes and error codes, and
  never code, arguments, paths or names: a solution appears as a hash of its file name. `Farol:UsageLogDirectory` sets
  where it goes, and files older than 30 days are deleted.
- `--usage-report [--days N]` sums the usage log up: the days with use, each tool's calls, errors and p50/p95
  durations, the errors by code and the loads per solution.

## [0.1.0-alpha.2] - 2026-10-08

### Added

- `--help` (also `-h`, `-?` and `/?`) prints the version, how an MCP client starts Farol and every option, then exits.
  It used to start the server, which waited for a client on stdin.

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

[0.1.0-alpha.5]: https://github.com/yuriafp/Farol/compare/v0.1.0-alpha.4...v0.1.0-alpha.5
[0.1.0-alpha.4]: https://github.com/yuriafp/Farol/compare/v0.1.0-alpha.3...v0.1.0-alpha.4
[0.1.0-alpha.3]: https://github.com/yuriafp/Farol/compare/v0.1.0-alpha.2...v0.1.0-alpha.3
[0.1.0-alpha.2]: https://github.com/yuriafp/Farol/compare/v0.1.0-alpha.1...v0.1.0-alpha.2
[0.1.0-alpha.1]: https://github.com/yuriafp/Farol/releases/tag/v0.1.0-alpha.1

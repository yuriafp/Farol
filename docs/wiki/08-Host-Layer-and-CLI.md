# Layer: Farol.Host & CLI

`Farol.Host` is the executable entry point of Farol. It handles command-line arguments, configures dependency injection, enforces standard stream isolation for the Model Context Protocol (MCP), and manages local privacy-first telemetry.

---

## 1. Subsystem Architecture

```
Farol.Host
├── Program.cs                # Entry point, pre-flight checks, hosting pipeline
├── HostConfiguration.cs      # CLI switch mappings and configuration cascading
├── ServerInstructions.cs     # Compact system prompt instructions injected into MCP clients
├── Help.cs                   # Command-line help text generator (--help)
├── UsageRecorder.cs          # Middleware filter recording tool calls and sessions
└── UsageReportCommand.cs     # CLI command parsing and summarizing local usage logs
```

---

## 2. Standard Stream Isolation (Stdio Transport)

Under the Model Context Protocol stdio transport, **standard input and standard output are reserved exclusively for JSON-RPC messages**.

### Strict Stderr Redirection
If any library, dependency, or logger writes plain text to `stdout`, the client's JSON-RPC parser will encounter a syntax error and terminate the connection.

Farol enforces strict isolation:
```csharp
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
```
- **Standard Output (`stdout`)**: Exclusively reserved for MCP JSON-RPC messages, one per line (newline-delimited JSON).
- **Standard Error (`stderr`)**: Captures all application logs, diagnostic traces, and informational messages.
- **Child Processes**: Spawned child processes (such as `dotnet` and `MSBuild.exe`) have their standard streams redirected and isolated; they never inherit Farol's stdout.

---

## 3. Configuration Precedence & Cascading

Configuration settings cascade in the following order of precedence (highest to lowest):

1. **Command-Line Arguments**: Passed directly when launching Farol (e.g. `--read-only`, `--root <dir>`).
2. **Environment Variables**: Prefixed with `Farol__` (e.g. `Farol__TrustedPaths__0=C:\repos`, `Farol__UsageLog=true`).
3. **`appsettings.json`**: Located in the application directory next to the executable (`AppContext.BaseDirectory`). *Note: Farol never reads `appsettings.json` from the repository being analyzed.*

### Command-Line Arguments & Options Reference

| Setting | Command Line | Default | Description |
|---|---|---|---|
| `Farol:RootDirectory` | `--root <dir>` | Current working directory | Where the default workspace is discovered. Automatically trusted. |
| `Farol:DefaultWorkspace` | `--workspace <path>` | Auto-discovered | Path to `.sln` or `.csproj` used when tool calls omit the `workspace` parameter. |
| `Farol:AutoLoad` | `--autoload <bool>` | `true` | Starts background workspace loading immediately upon server startup. |
| `Farol:ReadOnly` | `--read-only` | `false` | Blocks file writes, builds, and test runs. Tools explain the refusal. |
| `Farol:Offline` | `--offline` | `false` | Disables network access; package tools read only from local caches. |
| `Farol:UsageLog` | `--usage-log` | `false` | Records local privacy-first JSON Lines telemetry. |
| `Farol:UsageLogDirectory` | `--Farol:UsageLogDirectory <dir>` | `%LOCALAPPDATA%\Farol\usage` | Directory where daily usage logs are stored. |
| `Farol:TrustedPaths` | `--Farol:TrustedPaths:0 <dir>` | None | Additional directories authorized for loading, reading, and writing. |
| `Farol:BuildTimeoutMinutes`| `--Farol:BuildTimeoutMinutes <n>` | `15` | Maximum build duration before killing the entire process tree. |
| `Farol:TestTimeoutMinutes` | `--Farol:TestTimeoutMinutes <n>` | `20` | Maximum test execution duration per project. |
| (Command) | `--help`, `-h`, `-?`, `/?` | — | Prints version and command-line options to stdout, then exits without starting the server. |
| (Command) | `--usage-report [--days N]` | — | Sums up the usage log of the last N days (default 14) as plain text, then exits. |

---

## 4. Server Instructions (`ServerInstructions`)

When an MCP client connects, Farol transmits a compact instruction block (`ServerInstructions.Text`). Most agents add this text to their system prompt:

- **Token Economy**: The instruction set is kept under **1,000 characters** (~250 tokens), avoiding prompt bloat.
- **Guidance Highlights**:
  - Directs agents to use `dotnet_find_references`, `dotnet_call_hierarchy`, and `dotnet_hierarchy` instead of `grep`.
  - Recommends one `dotnet_check` when edits are done: the errors they introduced, dependent projects included, in seconds rather than a build.
  - Explains symbol formats (`Name`, `Type.Member`, an id from a result, `path:line`) and that paths are relative to the root, as responses write them, or to the workspace's folder.
- **Dynamic Policy Annotations**: When started with `--read-only` or `--offline`, Farol appends short policy notices explaining that write operations or network calls are disabled.

---

## 5. Local Privacy-First Usage Telemetry

When `--usage-log` is enabled, Farol records operational events to assist developers and teams in evaluating tool performance and identifying bottlenecks.

### Anonymization Rules
- **No Source Code**: Source code, snippets, diffs, and AST details are never recorded.
- **No File Paths or Symbol Names**: Paths and symbol queries are never written to logs.
- **Hashed Workspace Identifiers**: Solutions appear strictly as an 8-character SHA-256 hash of their filename.
- **Error Codes Only**: Failures record Farol's machine-readable error code (e.g. `path_not_trusted`, `invalid_argument`) and, for unexpected errors, only the exception's type name; never messages or stack traces.

### Generating Usage Reports (`--usage-report`)

To summarize local logs over the last 14 days (or a specified duration):

```bash
farol --usage-report --days 14
```

The output looks like this (illustrative numbers):

```text
Farol usage report: 2026-10-09 to 2026-10-22 (14 days), from C:\Users\<you>\AppData\Local\Farol\usage
It stays on this machine unless you share it.

days with use: 9 (10-09, 10-10, ...)
sessions: 23, Farol 0.1.0-alpha.4 (23)
clients: claude-code 2.1.286 (20), Visual Studio 18.0 (3)
tool calls: 1240, errors: 41 (3.3%), cancelled: 2, cut by the token budget: 17

tool                     calls  errors       p50       p95
dotnet_check               410       3    420 ms     1.6 s
dotnet_find_references     205       6     80 ms    610 ms
dotnet_find_symbols        190       9     15 ms     60 ms

errors by code: invalid_argument (21), symbol_not_found (14), workspace_not_ready (6)
workspaces, as a hash of the solution file name:
- 71cb0a41: 12 load(s), 0 failed, 8 project(s), load p50 3.5 s, slowest 12.5 s, load errors 0, warm-up p50 212 ms
crashes: none
```

# Layer: Farol.Engine — Workspaces & Loading

The Workspaces and Loading subsystem inside `Farol.Engine` is responsible for discovering, evaluating, loading, and tracking C# and VB.NET solutions. It provides the immutable snapshot foundation upon which all analysis, navigation, verification, and refactoring tools operate.

---

## 1. Subsystem Architecture

```
Farol.Engine (Workspaces & Loading)
├── Toolchain/
│   └── ToolchainProbe.cs           # Discovers .NET SDKs and Visual Studio installations via vswhere
├── Loading/
│   └── MSBuildWorkspaceLoader.cs   # Roslyn MSBuildWorkspace orchestrator with dual BuildHosts
└── Workspaces/
    ├── WorkspaceManager.cs         # Multi-session registry & default workspace resolution
    ├── WorkspaceDiscovery.cs       # Finds .sln/.slnx and project files (a .slnf only when named)
    ├── WorkspaceSession.cs         # Manages workspace lifecycle, background warmup, and snapshot refresh
    ├── WorkspaceSnapshot.cs        # Immutable snapshot representation (Solution, Catalog, Load Baseline)
    ├── WorkspaceTarget.cs          # Target identifier (solution vs standalone project)
    ├── WorkspaceWarmup.cs          # Background compilation & symbol index warming
    ├── WorkspaceWatcher.cs         # Multi-directory filesystem event monitor
    ├── ProjectCatalog.cs           # High-level index of projects, target frameworks, and configurations
    ├── ProjectFileInspector.cs     # Fast XML inspection of project formats and properties
    └── LoadReport.cs               # Diagnostic report of workspace load performance and issues
```

---

## 2. Toolchain Probing (`ToolchainProbe`)

To support both modern .NET and classic .NET Framework, Farol must identify the tools installed on the host machine.

### Visual Studio Discovery via `vswhere.exe`
On Windows, `ToolchainProbe` executes `vswhere.exe` from `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\`:
- Queries for installations with the `Microsoft.Component.MSBuild` workload.
- Resolves the newest Visual Studio instance (Visual Studio 2022, 2026, or Build Tools).
- Locates `MSBuild.exe` under `MSBuild\Current\Bin\MSBuild.exe`.
- Locates `vstest.console.exe` under `Common7\IDE\Extensions\TestPlatform\vstest.console.exe`.
- Sanitizes non-breaking spaces commonly inserted by Microsoft installers into product display names.

### .NET SDK Discovery
Executes `dotnet --version` within the target workspace directory. Because `dotnet` respects local `global.json` files, this probe identifies the exact active SDK version governing the repository.

---

## 3. MSBuild Workspace Loading (`MSBuildWorkspaceLoader`)

Farol uses Roslyn's `MSBuildWorkspace` configured with custom global properties to load full solution graphs.

### The Dual BuildHost Isolation Mechanism
When Roslyn loads a project, it delegates MSBuild evaluation to an external out-of-process BuildHost:
1. **Modern SDK-Style Projects**: The .NET SDK BuildHost executes using the modern .NET runtime.
2. **Classic .NET Framework Projects**: The `BuildHost-net472` executes using full .NET Framework 4.7.2+, invoking Visual Studio's MSBuild engine. This enables classic ASP.NET WebForms, WCF, ASMX, WinForms, and legacy VB.NET projects to resolve their MSBuild targets, GAC references, and design-time outputs.

### Automatic `VSToolsPath` Injection
Certain legacy projects (such as ASP.NET MVC 5 or WebApplication projects) use SDK-style project formats while importing legacy Visual Studio targets:
```xml
<Import Project="$(VSToolsPath)\WebApplications\Microsoft.WebApplication.targets" Condition="'$(VSToolsPath)' != ''" />
```
Because Roslyn routes SDK-style projects to the .NET SDK host by default, `VSToolsPath` would be undefined, causing evaluation to fail. Farol detects the installed Visual Studio via `ToolchainProbe` and automatically sets `VSToolsPath` in the workspace's global MSBuild properties:
```csharp
var path = Path.Combine(visualStudio.InstallationPath, "MSBuild", "Microsoft", "VisualStudio", $"v{visualStudio.Version.Split('.')[0]}.0");
```

### Unresolved Analyzer Stripping
In complex solutions, projects often reference internal source generators or analyzer projects that have not yet been compiled to disk. By default, Roslyn throws a fatal exception when attempting to search symbols in projects with missing analyzer binaries.
`MSBuildWorkspaceLoader.WithoutUnresolvedAnalyzers` intercepts `UnresolvedAnalyzerReference` entries, removes them from the in-memory `Solution`, and emits an informative warning advising the developer to build the generator project once.

### Eager Document Text Loading (`ReadAllTextAsync`)
Roslyn loads document source text lazily upon first access. If an agent modified a file on disk *after* the workspace was initialized but *before* Farol read that document, Roslyn would load the modified text as part of its original snapshot. This would contaminate the load baseline, rendering the edit invisible to `dotnet_check`.
To prevent this, `MSBuildWorkspaceLoader` eagerly reads the source text of every document in parallel (`ReadAllTextAsync`) during initialization, sealing the baseline.

### Design-Time Warning Reclassification
`MSBuildWorkspace` treats all messages from design-time builds as failures. When NuGet restore records package warnings (such as advisory warnings NU1901–NU1904), these were previously surfaced as load failures. Farol cross-references messages against `project.assets.json` and properly categorizes them as load warnings with their official NuGet codes.

---

## 4. The Workspace Session Lifecycle (`WorkspaceSession`)

A `WorkspaceSession` manages the lifecycle of a loaded solution or project:

```
[NotLoaded] ──► (StartLoading) ──► [Loading] ──► (Success) ──► [Ready]
                                        │                          │
                                        ▼ (Error)                  ▼
                                     [Failed]               (File Watcher / Edits)
                                        │                          │
                                        └── (ReloadAsync) ◄────────┘ (Project file edited)
```

### 1. Lazy Snapshot Refreshing (`RefreshAsync`)
When a tool requests a snapshot:
- If no filesystem changes have occurred and no edits are pending, the cached immutable `WorkspaceSnapshot` is returned immediately.
- If changes have occurred, `RefreshAsync` drains the pending file changes within a concurrency gate (`_refreshGate`), applies them, and returns an updated snapshot.

### 2. Eliminating Watcher Races (`NotifyChanged` & `NotifyEdited`)
Filesystem events (`FileSystemWatcher`) on Windows are asynchronous and subject to OS delivery latency. If an agent writes a file and immediately runs `dotnet_check`, the check might execute before the OS event fires.
Farol resolves this via two mechanisms:
- `NotifyChanged(paths)`: Invoked when Farol's own tools (like `dotnet_code_actions apply=true`) write files. Queues the paths immediately into memory.
- `NotifyEdited(paths)`: Accepts file paths the caller reports (the Claude Code edit hook passes `edited` to `dotnet_check`), so the next snapshot reads them from disk without waiting for the watcher. A claimed file the workspace does not compile joins it only when the file was created after the load and is outside `bin` and `obj`, so a file the project leaves out (`Compile Remove`) stays out.
- The file watcher follows the same rule: an edit to a file the workspace does not compile is ignored; a file joins only when it arrives (created, renamed or moved in), and a new file its writer still holds is read again on the next request.

### 3. Project File Reload vs. Document Update
- When a `.cs` or `.vb` source file changes, `WorkspaceSession` updates the document text inside the Roslyn `Solution` in memory without reloading the workspace.
- When a project or build file changes (`.csproj`, `.vbproj`, `.fsproj`, `.props`, `.targets`, `.sln`, `.slnx`, `.slnf`, or `global.json`, `packages.config`, `nuget.config`, `Directory.Build.rsp`), `WorkspaceSession` reloads the workspace (`ReloadAsync`), re-evaluating the entire MSBuild graph; a call made meanwhile waits for it.

### 4. Background Compilation Warm-Up (`WorkspaceWarmup`)
Immediately after a solution loads, Farol initiates a background task (`WorkspaceWarmup`):
- Eagerly compiles every project in the solution across all target frameworks.
- Warms Roslyn's internal declaration tables, symbol caches, and syntax trees.
- Builds the reference and inheritance indexes.
While warmup runs, tool calls execute safely against the ready snapshot. `dotnet_workspace` says the warm-up is still running (searches are slower until it ends), then how long it took, so benchmarks and clients know when full speed is reached.

---

## 5. Workspace Discovery & Ambiguity Handling (`WorkspaceManager`)

`WorkspaceManager` manages active sessions and resolves default workspaces.

### Multi-Solution Discovery Rules
1. If the tool call passes `workspace` (a `.sln`, `.slnx`, `.slnf`, `.csproj` or `.vbproj`, or a folder that holds one), that workspace is used.
2. If `workspace` is omitted, Farol uses `--workspace` when it was set; otherwise it discovers one in the root: a `.slnx` or `.sln` at the top of the root (a `.slnx` wins over a `.sln` of the same name), else a `.csproj` or `.vbproj` at the top, else solutions nested up to two folders deep (skipping `bin`, `obj`, `.git`, `.vs`, `.idea`, `node_modules`, `packages`, `artifacts`, `TestResults`). A `.slnf` is never discovered: pass it explicitly.
3. **Ambiguity Resolution (AC-4, AC-41)**: one candidate is used. With several or none, a call without `workspace` means the workspace already in use in this server process when there is exactly one; otherwise Farol returns `workspace_ambiguous` or `workspace_not_found`, listing the candidates (relative to the root) or the workspaces in use.
4. **Protected Directory Traversal**: folders that cannot be listed (such as `System Volume Information` or parts of a user profile) are passed over, so starting Farol at a drive root or a user profile works; the startup preload runs in the background and never stops the server.

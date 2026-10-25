# Layer: Farol.Tools & MCP Contract

`Farol.Tools` serves as the Model Context Protocol (MCP) interface layer. It defines and registers Farol's 18 MCP tools and 4 MCP prompts, validates inputs, manages token budgets, and formats responses into dense, LLM-optimized compact text.

---

## 1. Protocol Conventions & Metadata

- **Naming**: All tool names begin with the prefix `dotnet_` (e.g. `dotnet_check`, `dotnet_find_references`).
- **Language**: Tool names, parameter descriptions, error messages, and responses are in English.
- **Header Mirroring**: Every tool accepts an optional `workspace` parameter that is mirrored into the `Mcp-Param-Workspace` header.
- **Path Standards**: Paths in arguments and responses are relative to the root Farol runs in and use forward slashes (`/`). An argument may also be relative to the workspace's folder (AC-42); a response path outside the root, under `Farol:TrustedPaths`, is written in full.
- **Tool Annotations**:
  - `readOnlyHint: true`: Tools that do not modify files or execute arbitrary code.
  - `destructiveHint: true`: `dotnet_code_actions` and `dotnet_migration_plan` are annotated destructive because they can write (`apply=true`, `write=true`); a preview writes nothing.
  - `openWorldHint: true`: Tools that query external feeds (`packages`, `package_api`).
  - `dotnet_build` and `dotnet_test` are neither read-only nor destructive: they run the build and tests, and are refused under `--read-only`.

---

## 2. Infrastructure Components

- **`ArgumentCheck`**: Wraps every tool and checks the arguments against its input schema before the SDK binds them. A missing required argument, or one the SDK cannot bind (an object where a string goes), is an `invalid_argument` error that names it. A number written as a string, and null for an optional argument, pass, as the SDK binds them.
- **`ToolGuard`**: Runs every tool body and turns a `FarolException` into an MCP error whose text is the message plus the hint, so the agent reads what to do next.
- **`ToolParameters`**: The parameter conventions shared by every tool (`workspace`, `maxTokens`, `offset`) and the `Mcp-Param-Workspace` header name.
- **`ToolProgress`**: Turns engine progress messages into MCP progress notifications (for example during long workspace loads).

---

## 3. Comprehensive Tool Reference

Every tool also takes `workspace`; tools that return lists take `maxTokens` (default 1,500, up to 8,000), and the longest lists take `offset` for paging.

### Workspace Toolset

#### `dotnet_workspace`
Reports the status of the loaded solution, lists projects that failed to load, identifies the toolchain in use, and indicates whether the background warmup is complete.
- **Inputs**:
  - `action` (*string*, optional): `status` (default: report without waiting, starting a background load if needed), `load` (wait until loaded), `reload` (discard and load again).
  - `workspace` (*string*, optional): Path to solution or project file.
- **Annotations**: `readOnlyHint: true`

#### `dotnet_overview`
Provides a high-level map of an unfamiliar solution in a single call.
- **Inputs**:
  - `workspace` (*string*, optional): Target workspace path.
  - `maxTokens` (*int*, optional): Maximum token budget (default: 1500).
- **Output**: Total project count, languages (C#, VB), formats (SDK-style vs classic), target frameworks, app models (WebForms, MVC, WCF, WPF, WinForms), package management (CPM, packages.config), and the recommended working build command.
- **Annotations**: `readOnlyHint: true`

---

### Navigation Toolset

#### `dotnet_find_symbols`
Finds symbol declarations across the solution using fast parallel matching.
- **Inputs**:
  - `query` (*string*, required): Exact name, prefix, substring, or camel-case humps (`OrdCalc` $\rightarrow$ `OrderCalculator`).
  - `kind` (*string*, optional): `any` (default), `type`, `class`, `interface`, `struct`, `enum`, `record`, `delegate`, `method`, `property`, `field` or `event`.
  - `project` (*string*, optional): Restrict search to a specific project.
  - `maxTokens` (*int*, optional): Token limit (default: 1500).
- **Annotations**: `readOnlyHint: true`

#### `dotnet_symbol`
Inspects a specific symbol's signature, documentation comments, and implementation source.
- **Inputs**:
  - `symbol` (*string*, required): Symbol name, dotted name, documentation comment ID, or `path:line`.
  - `include` (*string*, optional): comma-separated: `signature` (always), `docs`, `source`; default `signature,docs`.
  - `workspace` (*string*, optional): Target workspace path.
- **Output**: Full signature, XML documentation summary, originating project/package, and source from the workspace, or decompiled with `ICSharpCode.Decompiler` for an external member; an external type returns a skeleton of its member signatures. Ambiguous queries return candidate lists (`AC-9`).
- **Annotations**: `readOnlyHint: true`

#### `dotnet_find_references`
Discovers compiler-accurate references across C#, VB.NET, and markup files.
- **Inputs**:
  - `symbol` (*string*, required): Symbol to search.
  - `kinds` (*string*, optional): Filter by reference kinds (`call`, `new`, `read`, `write`, `method group`, `reference`, `markup`).
  - `maxTokens` (*int*, optional): Token limit (default: 1500).
- **Features**: Includes object instantiations (`new`), tracks references through implemented interface members or base overrides (`AC-37`), and indexes markup references (.aspx, .xaml, .svc, .asmx).
- **Annotations**: `readOnlyHint: true`

#### `dotnet_hierarchy`
Navigates type inheritance and interface implementation graphs.
- **Inputs**:
  - `symbol` (*string*, required): Target type or member.
  - `direction` (*string*, optional): `all` (default), `base`, `derived` (downwards, including implementations) or `implementations`.
- **Annotations**: `readOnlyHint: true`

#### `dotnet_call_hierarchy`
Generates a recursive tree of callers or callees.
- **Inputs**:
  - `symbol` (*string*, required): Target method or property.
  - `direction` (*string*, optional): `callers` (default) or `callees`.
  - `depth` (*int*, optional): Traversal depth (1 to 3, default: 1).
- **Output**: Formatted tree showing signatures, files, lines and up to 5 call sites (file:line) per node.
- **Annotations**: `readOnlyHint: true`

#### `dotnet_outline`
Extracts a structural outline of a file or type without function bodies.
- **Inputs**:
  - `path` (*string*, optional): File path relative to the root, as responses write paths.
  - `symbol` (*string*, optional): Qualified type name.
- **Annotations**: `readOnlyHint: true`

---

### Verify Toolset

#### `dotnet_check`
Evaluates compiler diagnostics introduced by agent edits, suppressing pre-existing legacy warnings.
- **Inputs**:
  - `scope` (*string*, optional): `changed` (default), `file`, `project`, `solution`.
  - `path` (*string*, optional): Source file for `scope=file`.
  - `project` (*string*, optional): Project name for `scope=project`.
  - `includeExisting` (*bool*, optional): If true, lists pre-existing baseline diagnostics.
  - `format` (*string*, optional): `text` (default) or `hook` (Claude Code JSON hook format).
  - `edited` (*string*, optional): For editor hooks: the file just edited, read before checking so the answer never lags the edit.
- **Output**: Newly introduced compiler errors and warnings with `file:line` locations and descriptions.
- **Annotations**: `readOnlyHint: true`

#### `dotnet_code_actions`
Surfaces Visual Studio lightbulb quick-fixes and refactorings.
- **Inputs**:
  - `path` (*string*, required): File path relative to the root, as responses write paths.
  - `line` (*int*, required): 1-indexed line number.
  - `column` (*int*, optional): 1-indexed column number.
  - `diagnosticId` (*string*, optional): Specific compiler error ID (e.g. `CS0246`).
  - `action` (*string*, optional): Action number or title to preview or apply.
  - `apply` (*bool*, optional): If true, writes changes atomically and runs an immediate check.
- **Annotations**: `destructiveHint: true` (it can write with `apply=true`; a preview writes nothing)

---

### Build & Test Toolset

#### `dotnet_build`
Builds the workspace or project using the correct toolchain (`dotnet build` or Visual Studio `MSBuild.exe`).
- **Inputs**:
  - `project` (*string*, optional): Project name, as `dotnet_overview` lists it.
  - `configuration` (*string*, optional): Build configuration (default: `Debug`).
- **Output**: Status, exit code, elapsed duration, toolchain used, and deduplicated compiler errors/warnings extracted from the binary log.
- **Annotations**: Blocked under `--read-only`.

#### `dotnet_test`
Executes unit tests and reports failures concisely.
- **Inputs**:
  - `project` (*string*, optional): Test project name; default: every test project.
  - `filter` (*string*, optional): Only tests whose fully qualified name contains this text.
  - `affectedBy` (*string*, optional): Set to `changes` or a symbol name to run only impacted tests.
  - `configuration` (*string*, optional): Build configuration (default: `Debug`).
- **Output**: Summary counts, failure messages, and filtered user-code stack frames.
- **Annotations**: Blocked under `--read-only`.

---

### Packages Toolset

#### `dotnet_packages`
Analyzes direct and transitive package dependencies across projects.
- **Inputs**:
  - `project` (*string*, optional): Project name, as `dotnet_overview` lists it.
  - `include` (*string*, optional): `all` (default: direct and transitive), `direct`, `vulnerable`, `deprecated`, `outdated`; problem filters combine as 'any of'.
- **Features**: Supports CPM (`Directory.Packages.props`) and `packages.config`, traces transitive dependency chains, and displays security advisories.
- **Annotations**: `openWorldHint: true`

#### `dotnet_package_api`
Extracts the verified public API surface and XML documentation of an exact package version.
- **Inputs**:
  - `package` (*string*, required): NuGet package ID (e.g. `Newtonsoft.Json`).
  - `version` (*string*, optional): Exact version string.
  - `query` (*string*, optional): Type or member to find: exact name, prefix, camel-case humps or substring (best match only), `Type.Member`, or a documentation comment ID.
  - `targetFramework` (*string*, optional): Target framework folder to inspect.
- **Annotations**: `openWorldHint: true`

---

### Legacy Modernization Toolset

#### `dotnet_legacy_inventory`
Inventories legacy technologies, frameworks, and API usages.
- **Inputs**:
  - `project` (*string*, optional): Project name, as `dotnet_overview` lists it.
- **Output**: Counts and locations of classic projects, .NET Framework targets, packages.config, WebForms, ASMX, WCF, WinForms, WPF, `BinaryFormatter`, `System.Configuration`, binding redirects, and legacy hotspots.
- **Annotations**: `readOnlyHint: true`

#### `dotnet_config_inspect`
Inspects XML configuration files (`web.config`, `app.config`) with credentials masked.
- **Inputs**:
  - `path` (*string*, optional): Path to configuration file. If omitted, lists all configuration files.
- **Output**: App settings, connection strings (passwords masked), WCF service models, and the modern `appsettings.json` structural mapping.
- **Annotations**: `readOnlyHint: true`

#### `dotnet_portability`
Identifies .NET Framework APIs that are missing or obsolete on modern .NET targets.
- **Inputs**:
  - `targetFramework` (*string*, optional): Target framework (default: `net10.0`).
  - `project` (*string*, optional): Project name, as `dotnet_overview` lists it.
- **Output**: Grouped by technology, lists missing APIs, obsolete APIs with diagnostic codes, locations, and modern replacements.
- **Annotations**: `readOnlyHint: true`

#### `dotnet_migration_plan`
Generates a structured, dependency-ordered modernization strategy.
- **Inputs**:
  - `targetFramework` (*string*, optional): Migration target (default: `net10.0`).
  - `write` (*bool*, optional): If true, writes `assessment.md`, `plan.md`, and `tasks.md` to `docs/modernization/`.
- **Annotations**: `destructiveHint: true` (it can write with `write=true`; without it, nothing is written)

---

## 4. MCP Prompts Reference

Farol serves 4 high-level MCP prompts that clients expose as slash commands:

1. **`explore`** (`Explore a .NET solution`):
   Guides the agent to map the solution using `dotnet_overview`, search declarations using `dotnet_find_symbols`, inspect signatures via `dotnet_symbol`, and trace references via `dotnet_find_references` and `dotnet_call_hierarchy`.
2. **`verify_changes`** (`Verify my .NET edits`):
   Chains `dotnet_check` (scope `changed`) to verify new compiler errors, followed by `dotnet_build` for analyzer warnings, and `dotnet_test` with `affectedBy='changes'` to run affected unit tests.
3. **`upgrade_package`** (`Upgrade a NuGet package`):
   Audits current package usage via `dotnet_packages`, compares API surfaces between current and target versions using `dotnet_package_api`, highlights breaking changes, and verifies the upgrade with `dotnet_check` and `dotnet_test`.
4. **`modernize`** (`Plan a .NET Framework modernization`):
   Executes the full modernization playbook: `dotnet_legacy_inventory`, `dotnet_portability`, `dotnet_config_inspect`, and `dotnet_migration_plan`.

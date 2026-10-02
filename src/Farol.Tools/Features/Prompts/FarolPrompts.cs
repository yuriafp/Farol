using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Farol.Tools.Features.Prompts;

/// <summary>
/// Ready-made workflows clients offer as slash commands (MCP prompts): each one tells the agent which Farol tools to
/// chain, in which order, for a common .NET task.
/// </summary>
[McpServerPromptType]
public sealed class FarolPrompts
{
    [McpServerPrompt(Name = "explore", Title = "Explore a .NET solution")]
    [Description("Map the solution with Farol, then answer a question about the code with compiler-accurate navigation instead of text search.")]
    public static string Explore(
        [Description("What to find out, e.g. 'how is an order total computed?'. Optional.")] string? question = null) =>
        $"""
        Use the Farol tools to understand this .NET solution{(string.IsNullOrWhiteSpace(question) ? "" : $" and answer: {question.Trim()}")}

        1. Call dotnet_overview first: projects, languages, target frameworks, app models and the build command that works.
        2. Find declarations with dotnet_find_symbols, read them with dotnet_symbol (include='docs,source') and file shapes with dotnet_outline, instead of grep and whole-file reads.
        3. Follow the code with dotnet_find_references (it also finds WebForms, .asmx/.svc and XAML usages) and dotnet_call_hierarchy, and the type relationships with dotnet_hierarchy.
        4. Answer with path:line references for every claim.
        """;

    [McpServerPrompt(Name = "verify_changes", Title = "Verify my .NET edits")]
    [Description("Check, build and test the edits made so far, fixing what breaks.")]
    public static string VerifyChanges() =>
        """
        Verify the C#/VB edits made in this session with Farol, fixing problems as you go:

        1. dotnet_check (scope 'changed'): the compiler errors and warnings the edits introduced, including in projects that depend on them. Fix every new error and run it again until it reports none.
        2. dotnet_build: the full build with the toolchain that works for the solution, which also shows analyzer warnings.
        3. dotnet_test with affectedBy='changes': only the tests that reach the edited code. For each failure, read the assertion message and the stack frames in user code, then fix the code (not the test, unless the test is wrong).
        4. Summarize what was verified, what failed, and what you changed, with path:line references.
        """;

    [McpServerPrompt(Name = "upgrade_package", Title = "Upgrade a NuGet package")]
    [Description("Plan a NuGet package upgrade from the exact APIs of both versions and the code that uses them.")]
    public static string UpgradePackage(
        [Description("The package ID, e.g. Newtonsoft.Json.")] string package,
        [Description("The version to move to. Optional: the newest stable one by default.")] string? version = null) =>
        $"""
        Plan the upgrade of the NuGet package {package.Trim()} to {(string.IsNullOrWhiteSpace(version) ? "its newest stable version" : $"version {version.Trim()}")} with Farol:

        1. dotnet_packages: which projects use it, the versions they resolve (directly or transitively), and whether the current version is vulnerable or deprecated.
        2. Find the code that uses the package: dotnet_find_symbols and dotnet_find_references on its main types.
        3. dotnet_package_api for the current and the target version on those types and members: compare signatures, removed members and [Obsolete] markers instead of assuming the API is unchanged.
        4. List each breaking difference with the path:line of the code it affects, and the edit each one needs.
        5. Only after that, change the version (Directory.Packages.props, the project file or packages.config, wherever dotnet_packages says it lives) and run dotnet_check, dotnet_build and dotnet_test with affectedBy='changes'.
        """;

    [McpServerPrompt(Name = "modernize", Title = "Plan a .NET Framework modernization")]
    [Description("Assess a .NET Framework solution and plan its migration to modern .NET, without changing code.")]
    public static string Modernize(
        [Description("The migration target, e.g. net10.0 (default), net8.0 or netstandard2.0.")] string targetFramework = "net10.0") =>
        $"""
        Assess this .NET Framework solution and plan its migration to {targetFramework.Trim()} with Farol. Do not change any code.

        1. dotnet_legacy_inventory: the legacy technologies, where they are, and the files where they concentrate.
        2. dotnet_portability targetFramework={targetFramework.Trim()}: the APIs that are missing or obsolete on the target, grouped by technology, with the replacement for each.
        3. dotnet_config_inspect on each web.config and app.config it lists: what moves to appsettings.json, and which values are secrets.
        4. dotnet_migration_plan targetFramework={targetFramework.Trim()}: the ordered steps, one per project. Add write=true only if the user wants it saved as documents in the repository.
        5. Summarize: the blockers by technology (what needs a rewrite versus API replacements), the order of the steps and their sizes, and the tasks of the first step with their path:line references.
        """;
}

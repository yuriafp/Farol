namespace Farol.Host;

/// <summary>Sent to clients at connection time; most agents inject it into their system prompt, so keep it short.</summary>
internal static class ServerInstructions
{
    public const string Text = """
        Farol answers from the C# and VB compiler, for .NET Framework (classic projects, packages.config, WebForms, WCF, WinForms, WPF) and modern .NET solutions.
        - Who uses, calls or implements a symbol, and where it is declared: dotnet_find_references, dotnet_call_hierarchy, dotnet_hierarchy and dotnet_find_symbols resolve overloads and same-named members, cover VB, every target framework and WebForms/XAML markup, and skip comments and strings, which grep cannot.
        - When your edits are done, one dotnet_check lists the errors they introduced, dependent projects included, in seconds rather than a build.
        - dotnet_overview maps an unfamiliar solution; dotnet_outline and dotnet_symbol show a file's members or one member's code without reading whole files; dotnet_package_api gives a package version's exact API.
        - Symbols: a name, Type.Member, an id from a result, or path:line. Paths are relative to the root, as responses write them, or to the workspace's folder.
        """;

    private const string ReadOnlyNote = "\n- This server runs read-only: dotnet_code_actions returns diffs but cannot apply them, dotnet_migration_plan cannot write its documents, and dotnet_build and dotnet_test are refused.";

    private const string OfflineNote = "\n- This server runs offline: dotnet_packages does not ask the feeds, and dotnet_package_api reads only packages already in the local NuGet caches.";

    public static string For(bool readOnly, bool offline) => Text + (readOnly ? ReadOnlyNote : string.Empty) + (offline ? OfflineNote : string.Empty);
}

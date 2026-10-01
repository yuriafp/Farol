namespace Farol.Host;

/// <summary>Sent to clients at connection time; most agents inject it into their system prompt, so keep it short.</summary>
internal static class ServerInstructions
{
    public const string Text = """
        Farol gives you compiler-accurate understanding of C# and VB .NET solutions, from legacy .NET Framework (classic csproj/vbproj, packages.config, WebForms, WCF, ASMX, WinForms, WPF) to modern .NET.
        - In an unfamiliar .NET codebase, call dotnet_overview first: one call maps projects, target frameworks, app models and the build command that works.
        - For C#/VB symbols prefer dotnet_find_symbols, dotnet_find_references, dotnet_hierarchy and dotnet_call_hierarchy over grep: they resolve overloads, cover VB and every target framework, and never match comments or strings.
        - dotnet_find_references also reports markup usages (WebForms, .asmx/.svc, XAML): check it before deleting a handler or class that looks unused.
        - Read less: dotnet_outline shows a file's shape; dotnet_symbol with include='source' returns one member's code.
        - After editing .cs/.vb files, call dotnet_check: it reports only the errors and warnings your edits introduced, including in dependent projects, in about a second.
        - dotnet_code_actions offers the compiler's fixes and refactorings at a line (e.g. add a missing using) as a diff; apply=true writes it.
        - dotnet_build picks the toolchain that works (Visual Studio's MSBuild for classic projects). dotnet_test with affectedBy (a symbol, or 'changes') runs only the tests that reach your edits.
        - Pass symbols as names, dotted names, ids from earlier results, or path:line. Ambiguous names return candidates to choose from.
        - File and project edits are picked up automatically; dotnet_workspace reports load state and failed projects.
        - Paths in responses are relative to the workspace root, formatted as path:line.
        """;

    private const string ReadOnlyNote = "\n- This server runs read-only: dotnet_code_actions returns diffs but cannot apply them, and dotnet_build and dotnet_test are refused.";

    public static string For(bool readOnly) => readOnly ? Text + ReadOnlyNote : Text;
}

using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;

namespace Farol.Engine.CodeActions;

/// <summary>
/// The code fixes and refactorings Visual Studio offers out of the box, from Roslyn's Features assemblies. They are
/// MEF exports with parameterless importing constructors, so Farol creates one instance of each per process.
/// </summary>
internal static partial class CodeActionProviders
{
    private static readonly string[] AssemblyNames =
    [
        "Microsoft.CodeAnalysis.Features",
        "Microsoft.CodeAnalysis.CSharp.Features",
        "Microsoft.CodeAnalysis.VisualBasic.Features",
    ];

    private static readonly Lazy<Catalog> Instance = new(Discover);

    public static IEnumerable<CodeFixProvider> FixersFor(string language, string diagnosticId) =>
        Instance.Value.Fixers.Where(f => f.Languages.Contains(language) && f.DiagnosticIds.Contains(diagnosticId)).Select(f => f.Provider);

    public static IEnumerable<CodeRefactoringProvider> RefactoringsFor(string language) =>
        Instance.Value.Refactorings.Where(r => r.Languages.Contains(language)).Select(r => r.Provider);

    /// <summary>A short label from the provider type: CSharpAddImportCodeFixProvider → "add import".</summary>
    public static string Label(object provider)
    {
        var name = provider.GetType().Name;
        foreach (var prefix in (string[])["CSharp", "VisualBasic", "Abstract"])
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                name = name[prefix.Length..];
            }
        }

        foreach (var suffix in (string[])["CodeFixProvider", "CodeRefactoringProvider", "Provider"])
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return WordBoundary().Replace(name, " $1").Trim().ToLowerInvariant();
    }

    private static Catalog Discover()
    {
        var fixers = new List<(CodeFixProvider, ImmutableHashSet<string>, string[])>();
        var refactorings = new List<(CodeRefactoringProvider, string[])>();
        foreach (var type in AssemblyNames.SelectMany(LoadableTypes).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.IsAbstract || type.ContainsGenericParameters)
            {
                continue;
            }

            if (type.GetCustomAttribute<ExportCodeFixProviderAttribute>() is { } fix && Create<CodeFixProvider>(type) is { } fixer)
            {
                fixers.Add((fixer, [.. fixer.FixableDiagnosticIds], fix.Languages));
            }
            else if (type.GetCustomAttribute<ExportCodeRefactoringProviderAttribute>() is { } refactoring && Create<CodeRefactoringProvider>(type) is { } refactorer)
            {
                refactorings.Add((refactorer, refactoring.Languages));
            }
        }

        return new Catalog(fixers, refactorings);
    }

    private static IEnumerable<Type> LoadableTypes(string assemblyName)
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.Load(assemblyName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return [];
        }

        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private static T? Create<T>(Type type)
        where T : class
    {
        if (!typeof(T).IsAssignableFrom(type) || type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is not { } constructor)
        {
            return null;
        }

        try
        {
            return (T)constructor.Invoke(null);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    [GeneratedRegex("(?<=[a-z])([A-Z])")]
    private static partial Regex WordBoundary();

    private sealed class Catalog(
        List<(CodeFixProvider Provider, ImmutableHashSet<string> DiagnosticIds, string[] Languages)> fixers,
        List<(CodeRefactoringProvider Provider, string[] Languages)> refactorings)
    {
        public List<(CodeFixProvider Provider, ImmutableHashSet<string> DiagnosticIds, string[] Languages)> Fixers { get; } = fixers;

        public List<(CodeRefactoringProvider Provider, string[] Languages)> Refactorings { get; } = refactorings;
    }
}

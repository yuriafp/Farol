using Farol.Engine.Navigation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NuGet.Frameworks;
using NuGet.Packaging;

namespace Farol.Engine.Packages;

/// <summary>The assemblies of one target-framework folder of a package (ref/ when the package has one, otherwise lib/).</summary>
public sealed record PackageAssemblies(NuGetFramework Framework, string Folder, IReadOnlyList<string> Files, IReadOnlyList<NuGetFramework> Available);

/// <summary>A public type or member of a package, ready to print.</summary>
public sealed record ApiEntry(ISymbol Symbol, string Kind, string Signature, string? Summary, bool Obsolete);

/// <summary>
/// The public API of a package's assemblies, read with Roslyn from metadata: the exact signatures and XML
/// documentation of that version, never a guess. Compiled against the running .NET's own assemblies so that
/// framework types display by name.
/// </summary>
public sealed class PackageApi
{
    /// <summary>Like the navigation signature, plus modifiers (static, abstract…) that matter when calling an API.</summary>
    public static readonly SymbolDisplayFormat SignatureFormat = SymbolFormatter.Signature
        .AddMemberOptions(SymbolDisplayMemberOptions.IncludeModifiers)
        .AddParameterOptions(SymbolDisplayParameterOptions.IncludeExtensionThis);

    /// <summary>Type names for lists: containing types and type parameters, never a delegate's signature.</summary>
    public static readonly SymbolDisplayFormat TypeNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        delegateStyle: SymbolDisplayDelegateStyle.NameOnly,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public static readonly SymbolDisplayFormat MemberFormat = SignatureFormat.RemoveMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType);

    private static readonly Lazy<IReadOnlyList<(string Name, MetadataReference Reference)>> FrameworkAssemblies = new(LoadFrameworkAssemblies);

    private readonly List<IAssemblySymbol> _assemblies;
    private readonly CSharpCompilation _compilation;

    private PackageApi(CSharpCompilation compilation, List<IAssemblySymbol> assemblies, PackageAssemblies set)
    {
        _compilation = compilation;
        _assemblies = assemblies;
        Assemblies = set;
    }

    public PackageAssemblies Assemblies { get; }

    /// <summary>
    /// Picks the framework folder whose API to show: the nearest to <paramref name="target"/>, or without a target the
    /// newest .NET one, then .NET Standard, then .NET Framework. Reference assemblies (ref/) win over lib/ for the
    /// same framework because they are the compile-time contract.
    /// </summary>
    public static PackageAssemblies? SelectAssemblies(string packageFolder, NuGetFramework? target)
    {
        using var reader = new PackageFolderReader(packageFolder);
        var groups = reader.GetItems("ref").Select(g => (Group: g, Ref: true))
            .Concat(reader.GetLibItems().Select(g => (Group: g, Ref: false)))
            .Where(g => g.Group.Items.Any(IsAssembly))
            .ToList();
        if (groups.Count == 0)
        {
            return null;
        }

        var frameworks = groups.Select(g => g.Group.TargetFramework).Distinct().ToList();
        var chosen = target is not null
            ? NuGetFrameworkUtility.GetNearest(frameworks, target, f => f)
            : NuGetFrameworkUtility.GetNearest(frameworks, NuGetFramework.Parse("net10.0"), f => f)
                ?? NuGetFrameworkUtility.GetNearest(frameworks, NuGetFramework.Parse("net481"), f => f)
                ?? frameworks[0];
        if (chosen is null)
        {
            return null;
        }

        var best = groups.Where(g => g.Group.TargetFramework.Equals(chosen)).OrderByDescending(g => g.Ref).First().Group;
        var files = best.Items.Where(IsAssembly).Select(i => Path.GetFullPath(Path.Combine(packageFolder, i))).ToList();
        var folder = Path.GetDirectoryName(best.Items.First(IsAssembly))!.Replace('\\', '/');
        return new PackageAssemblies(chosen, folder, files, [.. frameworks.OrderBy(f => f.Framework, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Version)]);
    }

    public static PackageApi Open(PackageAssemblies set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var own = set.Files.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var references = new List<MetadataReference>();
        var ownReferences = new List<MetadataReference>();
        foreach (var file in set.Files)
        {
            var xml = Path.ChangeExtension(file, ".xml");
            var reference = MetadataReference.CreateFromFile(file, documentation: File.Exists(xml) ? XmlDocumentationProvider.CreateFromFile(xml) : null);
            ownReferences.Add(reference);
        }

        // The framework's own copy of a package assembly (System.Text.Json…) would clash with the package's version.
        references.AddRange(FrameworkAssemblies.Value.Where(a => !own.Contains(a.Name)).Select(a => a.Reference));
        references.AddRange(ownReferences);

        var compilation = CSharpCompilation.Create(
            "Farol.PackageApi",
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, metadataImportOptions: MetadataImportOptions.Public));
        var assemblies = ownReferences.Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>().ToList();
        return new PackageApi(compilation, assemblies, set);
    }

    /// <summary>Every public type of the package's assemblies, nested ones included, ordered by full name.</summary>
    public IReadOnlyList<INamedTypeSymbol> PublicTypes()
    {
        var types = new List<INamedTypeSymbol>();
        foreach (var assembly in _assemblies)
        {
            Collect(assembly.GlobalNamespace, types);
        }

        return [.. types.OrderBy(t => t.ToDisplayString(), StringComparer.Ordinal)];
    }

    /// <summary>Public and protected members a caller can use, without accessors and compiler-generated ones.</summary>
    public static IEnumerable<ISymbol> Members(INamedTypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.GetMembers().Where(m =>
            IsVisible(m)
            && !m.IsImplicitlyDeclared
            && m is not INamedTypeSymbol
            && m is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise or MethodKind.Destructor });
    }

    /// <summary>
    /// Types and members matching the query, best tier only: exact name, then case-insensitive, prefix, camel-case humps
    /// and substring. A dotted query ("JsonConvert.SerializeObject") also matches the containers; a documentation
    /// comment ID is looked up directly.
    /// </summary>
    public IReadOnlyList<ISymbol> Find(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var text = query.Trim();
        if (text.Length > 2 && text[1] == ':' && "TMPFE".Contains(text[0], StringComparison.Ordinal))
        {
            return [.. DocumentationCommentId.GetSymbolsForDeclarationId(text, _compilation).Where(s => _assemblies.Contains(s.ContainingAssembly, SymbolEqualityComparer.Default))];
        }

        var segments = text.Split('(')[0].Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Simplify).ToArray();
        if (segments.Length == 0)
        {
            return [];
        }

        var name = segments[^1];
        var qualifier = segments[..^1];
        var types = PublicTypes();
        var candidates = types.Cast<ISymbol>().Concat(types.SelectMany(Members)).Where(s => Qualifies(s, qualifier)).ToList();
        foreach (var tier in Tiers)
        {
            var matches = candidates.Where(s => tier(s.Name, name)).ToList();
            if (matches.Count > 0)
            {
                return [.. matches.OrderBy(s => s is INamedTypeSymbol ? 0 : 1).ThenBy(s => s.ToDisplayString(), StringComparer.Ordinal)];
            }
        }

        return [];
    }

    public static ApiEntry Describe(ISymbol symbol, bool withContainingType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var format = withContainingType ? SignatureFormat : MemberFormat;
        var signature = symbol is INamedTypeSymbol type ? type.ToDisplayString(SymbolFormatter.Signature) : symbol.ToDisplayString(format);
        if (symbol.DeclaredAccessibility is Accessibility.Protected or Accessibility.ProtectedOrInternal)
        {
            signature = "protected " + signature;
        }

        var obsolete = symbol.GetAttributes().Any(a => a.AttributeClass is { Name: "ObsoleteAttribute" });
        return new ApiEntry(symbol, SymbolFormatter.Kind(symbol), signature, SymbolFormatter.Summary(symbol, cancellationToken), obsolete);
    }

    private static readonly Func<string, string, bool>[] Tiers =
    [
        (name, query) => name.Equals(query, StringComparison.Ordinal),
        (name, query) => name.Equals(query, StringComparison.OrdinalIgnoreCase),
        (name, query) => name.StartsWith(query, StringComparison.OrdinalIgnoreCase),
        CamelHumps,
        (name, query) => name.Contains(query, StringComparison.OrdinalIgnoreCase),
    ];

    /// <summary>"SerObj" matches "SerializeObject": each capitalized chunk of the query starts a consecutive hump of the name.</summary>
    internal static bool CamelHumps(string name, string query)
    {
        var chunks = Humps(query);
        var humps = Humps(name);
        if (chunks.Count < 2 || chunks.Count > humps.Count)
        {
            return false;
        }

        for (var start = 0; start + chunks.Count <= humps.Count; start++)
        {
            var all = true;
            for (var i = 0; i < chunks.Count && all; i++)
            {
                all = humps[start + i].StartsWith(chunks[i], StringComparison.OrdinalIgnoreCase);
            }

            if (all)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> Humps(string text)
    {
        var humps = new List<string>();
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || char.IsUpper(text[i]) || text[i] == '_')
            {
                var hump = text[start..i].TrimStart('_');
                if (hump.Length > 0)
                {
                    humps.Add(hump);
                }

                start = i;
            }
        }

        return humps;
    }

    /// <summary>"List&lt;T&gt;" and "List`1" both mean List.</summary>
    private static string Simplify(string segment)
    {
        var cut = segment.IndexOfAny(['<', '`']);
        return cut > 0 ? segment[..cut] : segment;
    }

    private static bool Qualifies(ISymbol symbol, string[] qualifier)
    {
        if (qualifier.Length == 0)
        {
            return true;
        }

        var containers = new List<string>();
        for (var container = symbol.ContainingSymbol; container is not null and not INamespaceSymbol { IsGlobalNamespace: true }; container = container.ContainingSymbol)
        {
            containers.Insert(0, container.Name);
        }

        return containers.Count >= qualifier.Length
            && containers.Skip(containers.Count - qualifier.Length).SequenceEqual(qualifier, StringComparer.OrdinalIgnoreCase);
    }

    private static void Collect(INamespaceOrTypeSymbol container, List<INamedTypeSymbol> types)
    {
        foreach (var member in container.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol ns:
                    Collect(ns, types);
                    break;
                case INamedTypeSymbol type when IsVisible(type) && !type.IsImplicitlyDeclared && !type.Name.Contains('<', StringComparison.Ordinal):
                    types.Add(type);
                    Collect(type, types);
                    break;
            }
        }
    }

    private static bool IsVisible(ISymbol symbol)
    {
        for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAssembly(string path) =>
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        && !path.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>The running runtime's framework assemblies (the trusted platform assemblies in its shared framework directory).</summary>
    private static List<(string Name, MetadataReference Reference)> LoadFrameworkAssemblies()
    {
        var directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?.Split(Path.PathSeparator) ?? [];
        return [.. trusted
            .Where(p => string.Equals(Path.GetDirectoryName(p), directory, StringComparison.OrdinalIgnoreCase))
            .Select(p => (Path.GetFileNameWithoutExtension(p), (MetadataReference)MetadataReference.CreateFromFile(p)))];
    }
}

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using NuGet.Frameworks;
using NuGet.Packaging;
using ISymbol = Microsoft.CodeAnalysis.ISymbol;

namespace Farol.Engine.Navigation;

/// <summary>Decompiled C# of an external member and the assembly it came from (described without absolute paths).</summary>
public sealed record DecompiledSource(string Code, string Assembly);

/// <summary>
/// External (metadata) symbols: where their assembly comes from, and their source through decompilation
/// (ICSharpCode.Decompiler, the ILSpy engine). Reference assemblies have no method bodies, so they are mapped to the
/// implementation the code runs against: the package's lib/ folder, the installed shared framework, or the
/// .NET Framework directory.
/// </summary>
public static class MetadataSource
{
    private const int MaxForwardingDepth = 32;

    /// <summary>The file Roslyn read the symbol's assembly from, if it came from a file.</summary>
    public static string? AssemblyPath(Compilation compilation, ISymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(symbol);
        return symbol.ContainingAssembly is { } assembly && compilation.GetMetadataReference(assembly) is PortableExecutableReference { FilePath: { Length: > 0 } path }
            ? path
            : null;
    }

    /// <summary>
    /// "Newtonsoft.Json 13.0.3 · lib/net6.0/Newtonsoft.Json.dll", "Microsoft.NETCore.App.Ref 10.0.0 · ref/net10.0/System.Runtime.dll",
    /// ".NET Framework 4.8 reference assemblies · mscorlib.dll": where an assembly comes from, with no machine-specific path.
    /// </summary>
    public static string Describe(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        var full = Path.GetFullPath(assemblyPath);
        if (PackageOf(full) is { } package)
        {
            return $"{package.Id} {package.Version} · {Path.GetRelativePath(package.Folder, full).Replace('\\', '/')}";
        }

        var parts = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var packs = Array.FindLastIndex(parts, p => p.Equals("packs", StringComparison.OrdinalIgnoreCase));
        if (packs >= 0 && parts.Length > packs + 3)
        {
            return $"{parts[packs + 1]} {parts[packs + 2]} · {string.Join('/', parts[(packs + 3)..])}";
        }

        var shared = Array.FindLastIndex(parts, p => p.Equals("shared", StringComparison.OrdinalIgnoreCase));
        if (shared >= 0 && parts.Length == shared + 4)
        {
            return $"{parts[shared + 1]} {parts[shared + 2]} (installed runtime) · {parts[^1]}";
        }

        var framework = Array.FindIndex(parts, p => p.Equals(".NETFramework", StringComparison.OrdinalIgnoreCase));
        if (framework >= 0 && parts.Length > framework + 1)
        {
            return $".NET Framework {parts[framework + 1].TrimStart('v')} reference assemblies · {string.Join('/', parts[(framework + 2)..])}";
        }

        if (full.Contains($"{Path.DirectorySeparatorChar}Microsoft.NET{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return $".NET Framework (installed) · {parts[^1]}";
        }

        return parts[^1];
    }

    /// <summary>
    /// Decompiles one external member (or a whole type) from the implementation assembly behind
    /// <paramref name="assemblyPath"/>. Returns null when no implementation with bodies is found.
    /// </summary>
    public static DecompiledSource? Decompile(string assemblyPath, ISymbol symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentNullException.ThrowIfNull(symbol);
        if (Implementation(assemblyPath) is not { } implementation || (symbol as INamedTypeSymbol ?? symbol.ContainingType) is not { } type)
        {
            return null;
        }

        var settings = new DecompilerSettings(LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false, ShowXmlDocumentation = false };
        var typeName = new FullTypeName(ReflectionName(type));

        // Follow type forwarders: System.Runtime.dll forwards String to System.Private.CoreLib.dll.
        var path = implementation;
        for (var hop = 0; hop < MaxForwardingDepth; hop++)
        {
            using var file = Open(path);
            var decompiler = new CSharpDecompiler(file, new UniversalAssemblyResolver(path, false, file.DetectTargetFrameworkId()), settings);
            var definition = decompiler.TypeSystem.FindType(typeName).GetDefinition();
            if (definition is null)
            {
                return null;
            }

            if (definition.ParentModule?.MetadataFile?.FileName is { } defining && !string.Equals(Path.GetFullPath(defining), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                path = defining;
                continue;
            }

            string code;
            if (symbol is INamedTypeSymbol)
            {
                code = decompiler.DecompileTypeAsString(typeName);
            }
            else
            {
                var id = DocumentationCommentId.CreateDeclarationId(symbol) is { } raw ? SymbolFormatter.NormalizeId(raw) : null;
                var member = definition.Members.FirstOrDefault(m => SymbolFormatter.NormalizeId(IdStringProvider.GetIdString(m)) == id);
                if (member is null)
                {
                    return null;
                }

                code = decompiler.DecompileAsString(member.MetadataToken);
            }

            return new DecompiledSource(WithoutUsings(code), Describe(path));
        }

        return null;
    }

    /// <summary>
    /// The assembly with bodies behind a reference assembly: a package's lib/ twin of its ref/ folder, the installed
    /// shared framework for a targeting pack, the .NET Framework directory or GAC for .NET Framework reference
    /// assemblies. Any other assembly is its own implementation.
    /// </summary>
    internal static string? Implementation(string assemblyPath)
    {
        var full = Path.GetFullPath(assemblyPath);
        if (!File.Exists(full))
        {
            return null;
        }

        if (!IsReferenceAssembly(full))
        {
            return full;
        }

        var name = Path.GetFileName(full);
        var parts = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var refFolder = Array.FindLastIndex(parts, p => p.Equals("ref", StringComparison.OrdinalIgnoreCase));
        if (refFolder > 0 && refFolder == parts.Length - 3)
        {
            var root = string.Join(Path.DirectorySeparatorChar, parts[..refFolder]);
            var lib = Path.Combine(root, "lib");
            if (Directory.Exists(lib))
            {
                var framework = NuGetFramework.ParseFolder(parts[refFolder + 1]);
                var candidates = Directory.EnumerateDirectories(lib).Where(d => File.Exists(Path.Combine(d, name))).ToList();
                if (NuGetFrameworkUtility.GetNearest(candidates, framework, d => NuGetFramework.ParseFolder(Path.GetFileName(d))) is { } nearest)
                {
                    return Path.Combine(nearest, name);
                }
            }

            var packs = Array.FindLastIndex(parts, p => p.Equals("packs", StringComparison.OrdinalIgnoreCase));
            if (packs >= 0 && packs + 3 == refFolder && SharedFramework(string.Join(Path.DirectorySeparatorChar, parts[..packs]), parts[packs + 1], parts[packs + 2], name) is { } runtime)
            {
                return runtime;
            }
        }

        if (full.Contains(".NETFramework", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsWindows())
        {
            return NetFramework(name);
        }

        return null;
    }

    private static string? SharedFramework(string dotnetRoot, string pack, string version, string name)
    {
        var shared = Path.Combine(dotnetRoot, "shared", pack.EndsWith(".Ref", StringComparison.OrdinalIgnoreCase) ? pack[..^4] : pack);
        if (!Directory.Exists(shared))
        {
            return null;
        }

        // The targeting pack version (10.0.0) rarely matches the installed runtime (10.0.12): take the newest of the same major.minor.
        var majorMinor = string.Join('.', version.Split('.').Take(2)) + ".";
        var installed = Directory.EnumerateDirectories(shared)
            .Where(d => Path.GetFileName(d).StartsWith(majorMinor, StringComparison.Ordinal) && File.Exists(Path.Combine(d, name)))
            .OrderByDescending(d => NuGet.Versioning.NuGetVersion.TryParse(Path.GetFileName(d), out var v) ? v : null)
            .FirstOrDefault();
        return installed is null ? null : Path.Combine(installed, name);
    }

    private static string? NetFramework(string name)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var runtime = Path.Combine(windows, "Microsoft.NET");
        foreach (var candidate in new[]
        {
            Path.Combine(runtime, "Framework64", "v4.0.30319", name),
            Path.Combine(runtime, "Framework", "v4.0.30319", name),
            Path.Combine(runtime, "Framework64", "v4.0.30319", "WPF", name),
            Path.Combine(runtime, "Framework", "v4.0.30319", "WPF", name),
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var gac in new[] { "GAC_MSIL", "GAC_64", "GAC_32" })
        {
            var folder = Path.Combine(runtime, "assembly", gac, Path.GetFileNameWithoutExtension(name));
            if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, name, SearchOption.AllDirectories).FirstOrDefault() is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static bool IsReferenceAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return false;
            }

            var metadata = pe.GetMetadataReader();
            return metadata.IsAssembly && metadata.GetAssemblyDefinition().GetCustomAttributes().Any(handle =>
            {
                var constructor = metadata.GetCustomAttribute(handle).Constructor;
                var typeName = constructor.Kind switch
                {
                    HandleKind.MemberReference when metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent is { Kind: HandleKind.TypeReference } parent
                        => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name),
                    HandleKind.MethodDefinition => metadata.GetString(metadata.GetTypeDefinition(metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()).Name),
                    _ => null,
                };
                return typeName == "ReferenceAssemblyAttribute";
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return false;
        }
    }

    private static PEFile Open(string path)
    {
        using var stream = File.OpenRead(path);
        return new PEFile(path, stream, PEStreamOptions.PrefetchEntireImage);
    }

    /// <summary>"Ns.Outer+Inner`1", the reflection name the decompiler's type system uses.</summary>
    private static string ReflectionName(INamedTypeSymbol type)
    {
        var names = new Stack<string>();
        for (var current = type; current is not null; current = current.ContainingType)
        {
            names.Push(current.MetadataName);
        }

        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() + "." : string.Empty;
        return ns + string.Join('+', names);
    }

    private static string WithoutUsings(string code)
    {
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = 0;
        while (start < lines.Length && (lines[start].StartsWith("using ", StringComparison.Ordinal) || lines[start].Trim().Length == 0))
        {
            start++;
        }

        return string.Join('\n', lines[start..]).TrimEnd();
    }

    /// <summary>True when the assembly lives inside a NuGet package folder.</summary>
    public static bool IsPackageAssembly(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        return PackageOf(Path.GetFullPath(assemblyPath)) is not null;
    }

    private static bool IsPackageContent(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0].ToUpperInvariant() is "LIB" or "REF" or "RUNTIMES" or "ANALYZERS" or "BUILD" or "TOOLS";

    /// <summary>The NuGet package folder (global packages folder or packages.config folder) an assembly lives in.</summary>
    private static (string Id, string Version, string Folder)? PackageOf(string assemblyPath)
    {
        var folder = Path.GetDirectoryName(assemblyPath);
        for (var depth = 0; folder is not null && depth < 4; depth++, folder = Path.GetDirectoryName(folder))
        {
            try
            {
                if (!IsPackageContent(Path.GetRelativePath(folder, assemblyPath)))
                {
                    continue;
                }

                if (Directory.EnumerateFiles(folder, "*.nuspec").FirstOrDefault() is { } manifest)
                {
                    var nuspec = new NuspecReader(manifest);
                    return (nuspec.GetId(), nuspec.GetVersion().ToNormalizedString(), folder);
                }

                if (Directory.EnumerateFiles(folder, "*.nupkg").FirstOrDefault() is { } nupkg)
                {
                    using var archive = new PackageArchiveReader(nupkg);
                    var identity = archive.GetIdentity();
                    return (identity.Id, identity.Version.ToNormalizedString(), folder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidDataException or NuGet.Packaging.Core.PackagingException)
            {
                return null;
            }
        }

        return null;
    }
}

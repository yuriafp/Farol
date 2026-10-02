using System.Collections.Concurrent;
using Farol.Core;
using Farol.Engine.Navigation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace Farol.Engine.Legacy;

public enum ApiStatus
{
    Available,
    Missing,
    Obsolete,
}

/// <summary>Whether an API exists on the target framework, and if it is obsolete there, the diagnostic and message the compiler shows.</summary>
public sealed record ApiAvailability(ApiStatus Status, string? DiagnosticId = null, string? Message = null)
{
    public static ApiAvailability Available { get; } = new(ApiStatus.Available);

    public static ApiAvailability Missing { get; } = new(ApiStatus.Missing);
}

/// <summary>
/// The public API of a target framework, read from its reference assemblies (the targeting packs the .NET SDK installs,
/// or the same packs restored into the NuGet cache). Portability is a lookup of each used API here: missing means the
/// code will not compile, obsolete means the compiler warns or errors and the API may throw at run time.
/// </summary>
public sealed class TargetSurface
{
    private static readonly ConcurrentDictionary<(string Framework, bool Desktop, string Packages), Lazy<TargetSurface>> Cache = new();

    private readonly CSharpCompilation _compilation;
    private readonly ConcurrentDictionary<string, ApiAvailability> _checked = new(StringComparer.Ordinal);

    private TargetSurface(string targetFramework, bool windowsDesktop, string description, IEnumerable<string> assemblies)
    {
        TargetFramework = targetFramework;
        WindowsDesktop = windowsDesktop;
        Description = description;
        _compilation = CSharpCompilation.Create(
            "Farol.TargetSurface",
            references: assemblies.Select(a => MetadataReference.CreateFromFile(a)),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, metadataImportOptions: MetadataImportOptions.Public));
    }

    /// <summary>"net10.0", or "net10.0-windows" when the Windows Desktop pack (WinForms, WPF) is included.</summary>
    public string TargetFramework { get; }

    public bool WindowsDesktop { get; }

    /// <summary>The packs read, e.g. "Microsoft.NETCore.App.Ref 10.0.12, Microsoft.WindowsDesktop.App.Ref 10.0.12".</summary>
    public string Description { get; }

    /// <summary>
    /// Parses a migration target: .NET 5 and later (an "-windows" suffix includes WinForms and WPF) or .NET Standard 2.x.
    /// .NET Framework is what code migrates from, never a target.
    /// </summary>
    public static NuGetFramework ParseTarget(string targetFramework)
    {
        var framework = NuGetFramework.Parse(string.IsNullOrWhiteSpace(targetFramework) ? "net10.0" : targetFramework.Trim());
        var supported = (framework.Framework == FrameworkConstants.FrameworkIdentifiers.NetCoreApp && framework.Version.Major >= 5)
            || (framework.Framework == FrameworkConstants.FrameworkIdentifiers.NetStandard && framework.Version.Major == 2);
        return supported
            ? framework
            : throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"'{targetFramework}' is not a migration target.",
                "Use a modern .NET target framework such as net10.0, net8.0 or net10.0-windows, or netstandard2.0 for libraries that must still load on .NET Framework.");
    }

    /// <summary>The surface for a target, cached per process. Throws an actionable error when the targeting pack is not installed.</summary>
    public static TargetSurface Load(NuGetFramework target, bool windowsDesktop, string globalPackagesFolder)
    {
        ArgumentNullException.ThrowIfNull(target);
        windowsDesktop |= string.Equals(target.Platform, "windows", StringComparison.OrdinalIgnoreCase);
        windowsDesktop &= target.Framework == FrameworkConstants.FrameworkIdentifiers.NetCoreApp;
        var key = (target.GetShortFolderName(), windowsDesktop, globalPackagesFolder);
        return Cache.GetOrAdd(key, _ => new Lazy<TargetSurface>(() => Create(target, windowsDesktop, globalPackagesFolder))).Value;
    }

    public ApiAvailability Check(string documentationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentationId);
        return _checked.GetOrAdd(SymbolFormatter.NormalizeId(documentationId), id =>
        {
            var symbols = DocumentationCommentId.GetSymbolsForDeclarationId(id, _compilation);
            if (symbols.IsDefaultOrEmpty)
            {
                return ApiAvailability.Missing;
            }

            var symbol = symbols[0];
            return Obsolete(symbol) ?? (symbol.ContainingType is { } type ? Obsolete(type) : null) ?? ApiAvailability.Available;
        });
    }

    private static ApiAvailability? Obsolete(ISymbol symbol)
    {
        var attribute = symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass is { Name: "ObsoleteAttribute", ContainingNamespace.Name: "System" });
        if (attribute is null)
        {
            return null;
        }

        var message = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string : null;
        var diagnostic = attribute.NamedArguments.FirstOrDefault(a => a.Key == "DiagnosticId").Value.Value as string;
        return new ApiAvailability(ApiStatus.Obsolete, diagnostic, message);
    }

    private static TargetSurface Create(NuGetFramework target, bool windowsDesktop, string globalPackagesFolder)
    {
        var folder = target.GetShortFolderName().Split('-')[0];
        var packs = new List<(string Name, string Version, string Directory)>();
        if (target.Framework == FrameworkConstants.FrameworkIdentifiers.NetStandard)
        {
            packs.Add(target.Version.Minor >= 1
                ? Find("NETStandard.Library.Ref", folder, "ref", globalPackagesFolder)
                : Find("NETStandard.Library", folder, Path.Combine("build", folder, "ref"), globalPackagesFolder, packsFolder: false));
        }
        else
        {
            packs.Add(Find("Microsoft.NETCore.App.Ref", folder, "ref", globalPackagesFolder));
            if (windowsDesktop)
            {
                packs.Add(Find("Microsoft.WindowsDesktop.App.Ref", folder, "ref", globalPackagesFolder));
            }
        }

        return new TargetSurface(
            windowsDesktop ? folder + "-windows" : folder,
            windowsDesktop,
            string.Join(", ", packs.Select(p => $"{p.Name} {p.Version}")),
            packs.SelectMany(p => Directory.EnumerateFiles(p.Directory, "*.dll")));
    }

    /// <summary>
    /// The newest version of a pack that has assemblies for the framework folder: in the .NET installation's packs folder
    /// first, then in the global packages folder (where restore puts older targeting packs).
    /// </summary>
    private static (string Name, string Version, string Directory) Find(string pack, string framework, string assetsFolder, string globalPackagesFolder, bool packsFolder = true)
    {
        var roots = new List<string>();
        if (packsFolder)
        {
            roots.Add(Path.Combine(DotnetRoot(), "packs", pack));
        }

        roots.Add(Path.Combine(globalPackagesFolder, pack.ToLowerInvariant()));
        foreach (var root in roots.Where(Directory.Exists))
        {
            var newest = Directory.EnumerateDirectories(root)
                .Select(d => (Directory: d, Version: NuGetVersion.TryParse(Path.GetFileName(d), out var v) ? v : null))
                .Where(d => d.Version is not null)
                .Select(d => (d.Directory, d.Version, Assets: Path.Combine(d.Directory, assetsFolder.Contains(framework, StringComparison.Ordinal) ? assetsFolder : Path.Combine(assetsFolder, framework))))
                .Where(d => Directory.Exists(d.Assets) && Directory.EnumerateFiles(d.Assets, "*.dll").Any())
                .OrderByDescending(d => d.Version)
                .FirstOrDefault();
            if (newest.Assets is not null)
            {
                return (pack, newest.Version!.ToNormalizedString(), newest.Assets);
            }
        }

        throw new FarolException(
            ErrorCodes.InvalidArgument,
            $"The {pack} targeting pack for {framework} is not installed, so Farol cannot read the {framework} API.",
            $"Install the .NET SDK that targets {framework}, or restore any project that targets {framework} once (restore downloads the pack into the NuGet cache).");
    }

    /// <summary>The .NET installation running Farol: shared/Microsoft.NETCore.App/&lt;version&gt;/ is three levels below it.</summary>
    private static string DotnetRoot() =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));
}

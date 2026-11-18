using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Farol.Core;

namespace Farol.Engine.Conversion;

/// <summary>What a project conversion produces: the new project file, the files it deletes, and what it left or dropped.</summary>
public sealed record ProjectConversion(string ProjectFile, string OldText, string NewText, IReadOnlyList<string> DeletedFiles, IReadOnlyList<string> Notes);

public enum ConversionKind
{
    /// <summary>A classic project file becomes an SDK-style one with the same target framework.</summary>
    SdkStyle,

    /// <summary>packages.config becomes PackageReference items in the project file.</summary>
    PackageReference,
}

/// <summary>
/// Rewrites project files: a classic .csproj/.vbproj to SDK-style (keeping its .NET Framework target, files, references
/// and settings), and packages.config to PackageReference. The format changes; the build should not, so what the
/// converter cannot carry over faithfully is either kept as written (with a note) or refused.
/// </summary>
public static partial class ProjectConverter
{
    // Project types an SDK-style project can stand for: C#, VB, the test marker and WPF. Others (web applications, VSTO…) cannot.
    private static readonly HashSet<string> ConvertibleProjectTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}", // C#
        "{F184B08F-C81C-45F6-A57F-5ABD9991F28F}", // VB
        "{3AC096D0-A1C2-E12C-1390-A8335801FDAB}", // test project
        "{60DC8134-EBA5-43B8-BCC9-BB4BC16C2548}", // WPF
    };

    private static readonly Dictionary<string, string> KnownProjectTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{349C5851-65DF-11DA-9384-00065B846F21}"] = "a WebForms or MVC web application",
        ["{E3E379DF-F4C6-4180-9B81-6769533ABE47}"] = "an ASP.NET MVC 4 web application",
        ["{BAA0C2D2-18E2-41B9-852F-F413020CAA33}"] = "an Office (VSTO) add-in",
        ["{3D9AD99F-2412-4246-B90B-4EAA41C64699}"] = "a WCF service library",
        ["{A1591282-1198-4647-A2B1-27E5FF5F6F3B}"] = "a Silverlight project",
    };

    // The framework assemblies Microsoft.NET.Sdk references by itself for .NET Framework targets.
    private static readonly HashSet<string> ImplicitReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib", "System", "System.Core", "System.Data", "System.Drawing", "System.IO.Compression.FileSystem", "System.Numerics",
        "System.Runtime.Serialization", "System.Xml", "System.Xml.Linq",
    };

    // Properties the SDK sets itself the same way, or that only the classic format needs. The ones whose value can
    // matter (DefineConstants, Optimize, OutputPath, Deterministic) are handled one by one.
    private static readonly HashSet<string> DroppedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Configuration", "Platform", "ProjectGuid", "ProjectTypeGuids", "TargetFrameworkVersion", "TargetFrameworkProfile", "FileAlignment",
        "AppDesignerFolder", "IntermediateOutputPath", "DebugSymbols", "DebugType", "ErrorReport", "WarningLevel",
        "NuGetPackageImportStamp", "VisualStudioVersion", "VSToolsPath", "ReferencePath", "IsCodedUITest", "TestProjectType", "SchemaVersion",
        "ProductVersion", "OldToolsVersion", "UpgradeBackupLocation", "FileUpgradeFlags", "MyType", "OutputType", "RootNamespace", "AssemblyName",
        "StartupObject", "ProjectExtensions", "BootstrapperEnabled", "IsWebBootstrapper", "PublishUrl", "Install", "InstallFrom", "UpdateEnabled",
        "UpdateMode", "UpdateInterval", "UpdateIntervalUnits", "UpdatePeriodically", "UpdateRequired", "MapFileExtensions", "ApplicationRevision",
        "ApplicationVersion", "UseApplicationTrust", "RestorePackages", "SolutionDir", "DefineDebug", "DefineTrace",
        "DefineConstants", "Optimize", "OutputPath", "Deterministic",
    };

    // Item types the converter writes itself; any other one is kept as written.
    private static readonly HashSet<string> HandledItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile", "EmbeddedResource", "None", "Content", "Page", "ApplicationDefinition", "Resource", "Reference", "ProjectReference", "Import",
    };

    // Item types only Visual Studio's classic project system reads: dropped.
    private static readonly HashSet<string> DroppedItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "Folder", "Service", "BootstrapperPackage", "CodeAnalysisDependentAssemblyPaths",
    };

    public static ProjectConversion Convert(string projectFile, ConversionKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFile);
        var oldText = File.ReadAllText(projectFile);
        XDocument document;
        try
        {
            document = XDocument.Parse(oldText, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"{Path.GetFileName(projectFile)} is not valid XML: {ex.Message}");
        }

        var root = document.Root!;
        var sdkStyle = root.Attribute("Sdk") is not null || root.Elements().Any(e => e.Name.LocalName == "Sdk");
        var directory = Path.GetDirectoryName(projectFile)!;
        var packagesConfig = Path.Combine(directory, "packages.config");
        var packages = File.Exists(packagesConfig) ? ReadPackagesConfig(packagesConfig) : [];
        if (packages.Count > 0 && CentralVersions(directory) is { } central)
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"{Path.GetFileName(projectFile)} is under {central}, which manages package versions centrally: its packages would need PackageVersion entries there, and Farol does not edit that file. Nothing was written.",
                "Add the packages.config versions to Directory.Packages.props by hand, or convert outside central package management.");
        }

        return kind switch
        {
            ConversionKind.SdkStyle when sdkStyle => throw new FarolException(ErrorCodes.InvalidArgument, $"{Path.GetFileName(projectFile)} is already SDK-style.", packages.Count > 0 ? "kind=package_reference converts its packages.config." : null),
            ConversionKind.SdkStyle => ToSdkStyle(projectFile, oldText, root, directory, packages, packagesConfig),
            _ when packages.Count == 0 => throw new FarolException(ErrorCodes.InvalidArgument, $"{Path.GetFileName(projectFile)} has no packages.config to convert.", "Its packages are PackageReference items already, or it has none."),
            _ => ToPackageReference(projectFile, oldText, document, packages, packagesConfig),
        };
    }

    private sealed record Package(string Id, string Version, bool DevelopmentDependency);

    private static List<Package> ReadPackagesConfig(string path)
    {
        var document = XDocument.Load(path);
        return [.. document.Root!.Elements("package")
            .Select(p => new Package(
                (string?)p.Attribute("id") ?? string.Empty,
                (string?)p.Attribute("version") ?? string.Empty,
                string.Equals((string?)p.Attribute("developmentDependency"), "true", StringComparison.OrdinalIgnoreCase)))
            .Where(p => p.Id.Length > 0 && p.Version.Length > 0)];
    }

    // Directory.Packages.props above the project that turns central package management on.
    private static string? CentralVersions(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var file = Path.Combine(current.FullName, "Directory.Packages.props");
            if (File.Exists(file))
            {
                return File.ReadAllText(file).Contains("<ManagePackageVersionsCentrally>true", StringComparison.OrdinalIgnoreCase) ? file : null;
            }
        }

        return null;
    }

    // A path into the packages folder, however it starts (..\packages, $(SolutionDir)packages, packages\…): the segment after it.
    private static string? PackagesSegment(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var match = PackagesFolder().Match(path.Replace('/', '\\'));
        return match.Success ? match.Groups["rest"].Value : null;
    }

    [GeneratedRegex(@"(?:^|[\\)])packages\\(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackagesFolder();

    private static Package? PackageOf(string? path, IReadOnlyList<Package> packages) =>
        PackagesSegment(path) is { } rest ? packages.FirstOrDefault(p => rest.StartsWith($"{p.Id}.{p.Version}\\", StringComparison.OrdinalIgnoreCase)) : null;

    private static ProjectConversion ToPackageReference(string projectFile, string oldText, XDocument document, List<Package> packages, string packagesConfig)
    {
        var root = document.Root!;
        var notes = new List<string>();
        var ns = root.Name.Namespace;
        if (root.Descendants(ns + "Choose").Any())
        {
            throw Unsupported(Path.GetFileNameWithoutExtension(projectFile), "has Choose/When blocks", packages.Count > 0);
        }

        foreach (var reference in root.Descendants(ns + "Reference").Where(r => PackageOf(r.Element(ns + "HintPath")?.Value, packages) is not null).ToList())
        {
            RemoveWithWhitespace(reference);
        }

        // PackageReference brings the packages' analyzers and build files itself.
        foreach (var analyzer in root.Descendants(ns + "Analyzer").Where(a => PackageOf((string?)a.Attribute("Include"), packages) is not null).ToList())
        {
            RemoveWithWhitespace(analyzer);
        }

        foreach (var import in root.Elements(ns + "Import").Where(i => PackageOf((string?)i.Attribute("Project"), packages) is not null).ToList())
        {
            RemoveWithWhitespace(import);
        }

        foreach (var target in root.Elements(ns + "Target").Where(t => (string?)t.Attribute("Name") == "EnsureNuGetPackageBuildImports").ToList())
        {
            RemoveWithWhitespace(target);
        }

        foreach (var item in root.Descendants().Where(e => (string?)e.Attribute("Include") == "packages.config").ToList())
        {
            RemoveWithWhitespace(item);
        }

        RemoveEmptyItemGroups(root);
        var group = new XElement(ns + "ItemGroup");
        foreach (var element in PackageReferences(packages, ns))
        {
            group.Add(element);
        }

        InsertItemGroup(root, group);
        notes.Add(PackagesNote(packages.Count));
        foreach (var leftover in root.Descendants(ns + "Reference").Where(r => PackagesSegment(r.Element(ns + "HintPath")?.Value) is not null))
        {
            notes.Add($"kept the reference {(string?)leftover.Attribute("Include")}: its HintPath points into a packages folder, but not into a package packages.config lists");
        }

        return new ProjectConversion(projectFile, oldText, Serialize(document, oldText), [packagesConfig], notes);
    }

    private static string PackagesNote(int count) =>
        $"{count} package(s) become PackageReference items with the versions packages.config pinned; with PackageReference a package's content files, install.ps1 and config transforms no longer run, and packages only other packages need could be removed after a restore shows them as transitive";

    private static IEnumerable<XElement> PackageReferences(List<Package> packages, XNamespace ns) =>
        packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Select(p =>
        {
            var element = new XElement(ns + "PackageReference", new XAttribute("Include", p.Id), new XAttribute("Version", p.Version));
            if (p.DevelopmentDependency)
            {
                element.Add(new XElement(ns + "PrivateAssets", "all"));
            }

            return element;
        });

    private static FarolException Unsupported(string name, string reason, bool packages) => new(
        ErrorCodes.InvalidArgument,
        $"{name} {reason}, which the SDK-style format does not build the same way. Nothing was written.",
        packages ? "kind=package_reference still converts its packages.config." : "Leave its project file as it is; dotnet_migration_plan says how to move it.");

    private static ProjectConversion ToSdkStyle(string projectFile, string oldText, XElement root, string directory, List<Package> packages, string packagesConfig)
    {
        var ns = root.Name.Namespace;
        var notes = new List<string>();
        var visualBasic = projectFile.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);
        var name = Path.GetFileNameWithoutExtension(projectFile);
        var properties = root.Elements(ns + "PropertyGroup").ToList();
        string? Property(string property) => properties.Where(g => g.Attribute("Condition") is null).SelectMany(g => g.Elements(ns + property)).Select(e => e.Value.Trim()).LastOrDefault(v => v.Length > 0);

        var projectTypes = (Property("ProjectTypeGuids") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (projectTypes.FirstOrDefault(t => !ConvertibleProjectTypes.Contains(t)) is { } unsupported)
        {
            throw Unsupported(name, "is " + KnownProjectTypes.GetValueOrDefault(unsupported, $"a project of type {unsupported}"), packages.Count > 0);
        }

        if (root.Descendants(ns + "Choose").Any())
        {
            throw Unsupported(name, "has Choose/When blocks", packages.Count > 0);
        }

        var framework = TargetFramework(Property("TargetFrameworkVersion"))
            ?? throw new FarolException(ErrorCodes.InvalidArgument, $"{name} names no .NET Framework version (TargetFrameworkVersion) Farol can map to a target framework.");
        var wpf = projectTypes.Contains("{60DC8134-EBA5-43B8-BCC9-BB4BC16C2548}", StringComparer.OrdinalIgnoreCase)
            || root.Descendants(ns + "Page").Any() || root.Descendants(ns + "ApplicationDefinition").Any();
        var winForms = root.Descendants(ns + "Reference").Any(r => ReferenceName(r) == "System.Windows.Forms");

        var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"));
        var main = new XElement("PropertyGroup", new XElement("TargetFramework", framework));
        if (Property("OutputType") is { } outputType && !string.Equals(outputType, "Library", StringComparison.OrdinalIgnoreCase))
        {
            main.Add(new XElement("OutputType", outputType));
        }

        foreach (var (property, defaultValue) in new[] { ("RootNamespace", name), ("AssemblyName", name) })
        {
            if (Property(property) is { } value && !string.Equals(value, defaultValue, StringComparison.Ordinal))
            {
                main.Add(new XElement(property, value));
            }
        }

        if (Property("StartupObject") is { Length: > 0 } startup)
        {
            main.Add(new XElement("StartupObject", startup));
        }

        // The output stays in bin\<Configuration>\, where the classic project wrote it and other files may expect it.
        main.Add(new XElement("AppendTargetFrameworkToOutputPath", "false"));
        if (wpf)
        {
            main.Add(new XElement("UseWPF", "true"));
        }

        if (winForms)
        {
            main.Add(new XElement("UseWindowsForms", "true"));
        }

        // AssemblyInfo.cs already declares what the SDK would generate.
        if (root.Descendants(ns + "Compile").Any(c => Path.GetFileName((string?)c.Attribute("Include") ?? string.Empty).StartsWith("AssemblyInfo.", StringComparison.OrdinalIgnoreCase)))
        {
            main.Add(new XElement("GenerateAssemblyInfo", "false"));
        }

        if (visualBasic)
        {
            // The project's own Imports items stay, and are the only ones, as before.
            main.Add(new XElement("DisableImplicitNamespaceImports", "true"));
            if (Property("MyType") is { } myType)
            {
                main.Add(new XElement("MyType", myType));
            }
        }

        foreach (var group in properties.Where(g => g.Attribute("Condition") is null))
        {
            foreach (var element in Kept(group, null, ns))
            {
                if (main.Element(element.Name.LocalName) is null)
                {
                    main.Add(element);
                }
            }
        }

        project.Add(main);

        // Settings that differ by configuration and that the SDK does not set itself.
        foreach (var group in properties.Where(g => g.Attribute("Condition") is not null))
        {
            var condition = (string)group.Attribute("Condition")!;
            var kept = Kept(group, condition, ns);
            if (kept.Count > 0)
            {
                project.Add(new XElement("PropertyGroup", new XAttribute("Condition", condition), kept));
            }
        }

        // References: framework assemblies the SDK does not add, assemblies by path, packages, projects.
        var references = new XElement("ItemGroup");
        foreach (var reference in root.Descendants(ns + "Reference"))
        {
            var hint = reference.Element(ns + "HintPath")?.Value;
            if (PackageOf(hint, packages) is not null)
            {
                continue;
            }

            var metadata = reference.Elements().Where(m => m.Name.LocalName is not ("RequiredTargetFramework" or "HintPath")).Select(Strip).ToList();
            var condition = ConditionOf(reference);
            if (hint is null && ImplicitReferences.Contains(ReferenceName(reference)) && metadata.Count == 0 && condition is null)
            {
                continue;
            }

            references.Add(new XElement(
                "Reference",
                new XAttribute("Include", (string)reference.Attribute("Include")!),
                condition is null ? null : new XAttribute("Condition", condition),
                hint is null ? null : new XElement("HintPath", hint),
                metadata));
        }

        foreach (var element in PackageReferences(packages, XNamespace.None))
        {
            references.Add(element);
        }

        foreach (var reference in root.Descendants(ns + "ProjectReference"))
        {
            var condition = ConditionOf(reference);
            references.Add(new XElement(
                "ProjectReference",
                new XAttribute("Include", (string)reference.Attribute("Include")!),
                condition is null ? null : new XAttribute("Condition", condition),
                reference.Elements().Where(m => m.Name.LocalName is not ("Project" or "Name")).Select(Strip)));
        }

        if (visualBasic)
        {
            foreach (var import in root.Descendants(ns + "Import").Where(i => i.Parent?.Name.LocalName == "ItemGroup"))
            {
                references.Add(new XElement("Import", new XAttribute("Include", (string)import.Attribute("Include")!)));
            }
        }

        if (references.HasElements)
        {
            project.Add(references);
        }

        project.Add(Items(root, ns, directory, visualBasic, main, notes));

        // Imports: the SDK brings the language targets; package imports come back with PackageReference; others are kept.
        foreach (var import in root.Elements(ns + "Import"))
        {
            var path = (string?)import.Attribute("Project") ?? string.Empty;
            if (path.Contains("Microsoft.Common.props", StringComparison.OrdinalIgnoreCase) || path.Contains("Microsoft.CSharp.targets", StringComparison.OrdinalIgnoreCase)
                || path.Contains("Microsoft.VisualBasic.targets", StringComparison.OrdinalIgnoreCase) || path.Contains("Microsoft.TestTools.targets", StringComparison.OrdinalIgnoreCase)
                || PackageOf(path, packages) is not null)
            {
                continue;
            }

            project.Add(new XElement("Import", import.Attributes().Select(a => new XAttribute(a.Name.LocalName, a.Value))));
            notes.Add($"kept the import of {path}: check that it still applies to an SDK-style project");
        }

        foreach (var target in root.Elements(ns + "Target").Where(t => (string?)t.Attribute("Name") != "EnsureNuGetPackageBuildImports"))
        {
            notes.Add($"kept the target {(string?)target.Attribute("Name")}: check that it still runs at the right time");
            project.Add(Strip(target));
        }

        var deleted = new List<string>();
        if (packages.Count > 0)
        {
            deleted.Add(packagesConfig);
            notes.Add(PackagesNote(packages.Count) + "; an SDK-style project cannot use packages.config");
        }

        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "  ", NewLineChars = LineBreak(oldText) }))
        {
            new XDocument(project).Save(writer);
        }

        return new ProjectConversion(projectFile, oldText, builder.ToString() + LineBreak(oldText), deleted, notes);
    }

    /// <summary>
    /// A property group's settings the SDK-style project still needs: those it does not set itself, constants beyond
    /// DEBUG and TRACE, a non-default output path or optimization, and non-deterministic builds.
    /// </summary>
    private static List<XElement> Kept(XElement group, string? condition, XNamespace ns)
    {
        var kept = new List<XElement>();
        var debug = condition?.Contains("Debug", StringComparison.OrdinalIgnoreCase) == true;
        var release = condition?.Contains("Release", StringComparison.OrdinalIgnoreCase) == true;
        foreach (var element in group.Elements().Where(e => e.Value.Trim().Length > 0))
        {
            var value = element.Value.Trim();
            var keep = element.Name.LocalName switch
            {
                "DefineConstants" => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(c => c is not ("DEBUG" or "TRACE")),
                "Optimize" => (debug && value.Equals("true", StringComparison.OrdinalIgnoreCase)) || (release && value.Equals("false", StringComparison.OrdinalIgnoreCase)) || (!debug && !release && condition is not null),
                "OutputPath" => !DefaultOutputPath().IsMatch(value),
                "Deterministic" => value.Equals("false", StringComparison.OrdinalIgnoreCase),
                var other => !DroppedProperties.Contains(other),
            };
            if (keep)
            {
                kept.Add(new XElement(element.Name.LocalName, element.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => new XAttribute(a.Name.LocalName, a.Value)), value));
            }
        }

        _ = ns;
        return kept;
    }

    [GeneratedRegex(@"^bin\\(Debug|Release)\\?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DefaultOutputPath();

    // The item's own condition, else its group's.
    private static string? ConditionOf(XElement item) => (string?)item.Attribute("Condition") ?? (string?)item.Parent?.Attribute("Condition");

    /// <summary>
    /// The items the SDK's globs would not reproduce: files on disk the project leaves out (Remove), files outside its
    /// folder or under a condition (Include), metadata on included files (Update), and every item type the converter
    /// does not know, kept as written.
    /// </summary>
    private static List<XElement> Items(XElement root, XNamespace ns, string directory, bool visualBasic, XElement main, List<string> notes)
    {
        var groups = new List<XElement>();
        var sourceExtension = visualBasic ? ".vb" : ".cs";
        foreach (var (itemName, extension, switchName) in new[] { ("Compile", sourceExtension, "EnableDefaultCompileItems"), ("EmbeddedResource", ".resx", "EnableDefaultEmbeddedResourceItems") })
        {
            var listed = root.Descendants(ns + itemName).Where(e => e.Attribute("Include") is not null).ToList();
            var group = new XElement("ItemGroup");

            // A wildcard or a property in an Include cannot be matched with the globs: the items stay as written, without globs.
            if (listed.Any(e => ((string)e.Attribute("Include")!).IndexOfAny(['*', '?', '$', '@']) >= 0))
            {
                main.Add(new XElement(switchName, "false"));
                foreach (var item in listed)
                {
                    group.Add(WithCondition(Strip(item), ConditionOf(item)));
                }

                notes.Add($"the {itemName} items use wildcards or properties, so they are kept as written and the SDK's default {itemName} globs are turned off");
                groups.Add(group);
                continue;
            }

            var unconditioned = listed.Where(e => ConditionOf(e) is null).Select(e => Full(directory, (string)e.Attribute("Include")!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Globbed(directory, extension).Where(f => !unconditioned.Contains(f)).Order(StringComparer.OrdinalIgnoreCase))
            {
                group.Add(new XElement(itemName, new XAttribute("Remove", Path.GetRelativePath(directory, file))));
            }

            foreach (var item in listed)
            {
                var include = (string)item.Attribute("Include")!;
                var full = Full(directory, include);
                var condition = ConditionOf(item);
                var metadata = item.Elements().Where(m => !(m.Name.LocalName == "SubType" && m.Value is "Code" or "Component" or "Form" or "UserControl")).Select(Strip).ToList();
                if (condition is not null || !Globs(directory, full, extension))
                {
                    group.Add(WithCondition(new XElement(itemName, new XAttribute("Include", include), metadata), condition));
                }
                else if (metadata.Count > 0)
                {
                    group.Add(new XElement(itemName, new XAttribute("Update", include), metadata));
                }
            }

            if (group.HasElements)
            {
                groups.Add(group);
            }
        }

        var content = new XElement("ItemGroup");
        foreach (var item in root.Descendants().Where(e => e.Name.LocalName is "Content" or "None" && e.Attribute("Include") is not null && e.Parent?.Name.LocalName == "ItemGroup"))
        {
            var include = (string)item.Attribute("Include")!;
            var metadata = item.Elements().Select(Strip).ToList();
            var condition = ConditionOf(item);
            var inside = Full(directory, include).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && include.IndexOfAny(['*', '?', '$']) < 0;
            if (string.Equals(include, "packages.config", StringComparison.OrdinalIgnoreCase) || (item.Name.LocalName == "None" && metadata.Count == 0 && inside && condition is null))
            {
                continue;
            }

            if (item.Name.LocalName == "Content")
            {
                if (inside)
                {
                    content.Add(new XElement("None", new XAttribute("Remove", include)));
                }

                content.Add(WithCondition(new XElement("Content", new XAttribute("Include", include), metadata), condition));
            }
            else
            {
                // The None glob has the files of the folder: Update them. Others are not globbed: Include them.
                content.Add(inside && condition is null
                    ? new XElement("None", new XAttribute("Update", include), metadata)
                    : WithCondition(new XElement("None", new XAttribute("Include", include), metadata), condition));
            }
        }

        foreach (var item in root.Descendants().Where(e => e.Name.LocalName is "Page" or "ApplicationDefinition" or "Resource" && e.Parent?.Name.LocalName == "ItemGroup"))
        {
            var include = (string?)item.Attribute("Include") ?? string.Empty;
            if (item.Name.LocalName == "Resource")
            {
                // The SDK does not glob WPF resources.
                content.Add(WithCondition(new XElement("Resource", new XAttribute("Include", include), item.Elements().Select(Strip)), ConditionOf(item)));
                continue;
            }

            var metadata = item.Elements().Where(m => m.Name.LocalName is not ("Generator" or "SubType")).ToList();
            if (metadata.Count > 0)
            {
                content.Add(new XElement(item.Name.LocalName, new XAttribute("Update", include), metadata.Select(Strip)));
            }
        }

        if (content.HasElements)
        {
            groups.Add(content);
        }

        var other = new XElement("ItemGroup");
        foreach (var item in root.Elements(ns + "ItemGroup").SelectMany(g => g.Elements()).Where(e => !HandledItems.Contains(e.Name.LocalName) && !DroppedItems.Contains(e.Name.LocalName)))
        {
            other.Add(WithCondition(Strip(item), ConditionOf(item)));
        }

        if (other.HasElements)
        {
            notes.Add($"kept {string.Join(", ", other.Elements().Select(e => e.Name.LocalName).Distinct(StringComparer.OrdinalIgnoreCase))} item(s) as written: check that the SDK-style build still uses them");
            groups.Add(other);
        }

        return groups;
    }

    private static XElement WithCondition(XElement element, string? condition)
    {
        if (condition is not null && element.Attribute("Condition") is null)
        {
            element.SetAttributeValue("Condition", condition);
        }

        return element;
    }

    // What Microsoft.NET.Sdk's default globs include: files under the project folder, outside its own bin and obj and dot-folders.
    private static IEnumerable<string> Globbed(string directory, string extension) =>
        Directory.EnumerateFiles(directory, "*" + extension, SearchOption.AllDirectories).Where(f => Globs(directory, f, extension));

    private static bool Globs(string directory, string file, string extension)
    {
        if (!file.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetExtension(file), extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar);
        return !(segments.Length > 1 && (segments[0].Equals("bin", StringComparison.OrdinalIgnoreCase) || segments[0].Equals("obj", StringComparison.OrdinalIgnoreCase)))
            && !segments.SkipLast(1).Any(s => s.StartsWith('.'));
    }

    private static string Full(string directory, string include) =>
        Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(include).Replace('\\', Path.DirectorySeparatorChar)));

    private static string ReferenceName(XElement reference) => ((string?)reference.Attribute("Include") ?? string.Empty).Split(',')[0].Trim();

    private static string? TargetFramework(string? version)
    {
        if (version is null || !version.StartsWith('v'))
        {
            return null;
        }

        var digits = version[1..].Replace(".", string.Empty, StringComparison.Ordinal);
        return digits.Length is >= 2 and <= 3 && digits.All(char.IsDigit) ? "net" + digits.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static XElement Strip(XElement element) =>
        new(element.Name.LocalName, element.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => new XAttribute(a.Name.LocalName, a.Value)), element.Nodes().Select(n => n is XElement e ? Strip(e) : n));

    private static string LineBreak(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static void RemoveWithWhitespace(XElement element)
    {
        if (element.PreviousNode is XText { Value: var text } whitespace && string.IsNullOrWhiteSpace(text))
        {
            whitespace.Remove();
        }

        element.Remove();
    }

    private static void RemoveEmptyItemGroups(XElement root)
    {
        foreach (var group in root.Elements(root.Name.Namespace + "ItemGroup").Where(g => !g.HasElements).ToList())
        {
            RemoveWithWhitespace(group);
        }
    }

    // A new item group after the last one, indented like its siblings.
    private static void InsertItemGroup(XElement root, XElement group)
    {
        var indent = root.Elements().FirstOrDefault()?.PreviousNode is XText { Value: var text } ? text.TrimStart('\r', '\n') : "  ";
        var lineBreak = root.Elements().FirstOrDefault()?.PreviousNode is XText { Value: var first } && first.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        foreach (var child in group.Elements().ToList())
        {
            child.AddBeforeSelf(new XText(lineBreak + indent + indent));
            foreach (var metadata in child.Elements().ToList())
            {
                metadata.AddBeforeSelf(new XText(lineBreak + indent + indent + indent));
            }

            if (child.HasElements)
            {
                child.Add(new XText(lineBreak + indent + indent));
            }
        }

        group.Add(new XText(lineBreak + indent));
        var anchor = root.Elements(root.Name.Namespace + "ItemGroup").LastOrDefault();
        if (anchor is not null)
        {
            anchor.AddAfterSelf(group);
            anchor.AddAfterSelf(new XText(lineBreak + indent));
        }
        else
        {
            root.Add(new XText(indent), group, new XText(lineBreak));
        }
    }

    private static string Serialize(XDocument document, string oldText)
    {
        var builder = new StringBuilder();
        if (document.Declaration is { } declaration)
        {
            builder.Append(declaration).Append(LineBreak(oldText));
        }

        builder.Append(document.Root!.ToString(SaveOptions.DisableFormatting));
        if (oldText.EndsWith('\n'))
        {
            builder.Append(LineBreak(oldText));
        }

        return builder.ToString();
    }
}

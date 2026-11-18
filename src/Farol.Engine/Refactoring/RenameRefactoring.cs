using System.Text;
using System.Text.RegularExpressions;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Engine.CodeActions;
using Farol.Engine.Markup;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using CSharpFacts = Microsoft.CodeAnalysis.CSharp.SyntaxFacts;
using CSharpKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind;
using VisualBasicFacts = Microsoft.CodeAnalysis.VisualBasic.SyntaxFacts;
using VisualBasicKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;

namespace Farol.Engine.Refactoring;

/// <summary>A rename computed on a snapshot: the file edits (moves are a removal plus a creation), the moves, and what was left as is.</summary>
public sealed record RenameResult(string OldName, string NewName, string Symbol, CodeActionChanges Changes, IReadOnlyList<SourceMove> Moves, IReadOnlyList<string> Notes);

/// <summary>
/// Renames a symbol: Roslyn's <see cref="Renamer"/> for C# and VB, then Farol's own edits for markup (WebForms, XAML),
/// for the file named after a type (with its XAML or resource companions and the project file items), and a check that
/// every reference still binds to the renamed symbol.
/// </summary>
public static partial class RenameRefactoring
{
    private const int MaxConflictsShown = 10;

    // Renaming one of these changes a page's URL: their code-behind keeps its file name.
    private static readonly string[] WebMarkupExtensions = [".aspx", ".ascx", ".master", ".asmx", ".svc", ".ashx", ".asax"];

    // Files that travel with a code file of the same name.
    private static readonly string[] CompanionExtensions = [".resx", ".xaml"];

    public static async Task<RenameResult> ComputeAsync(WorkspaceSnapshot snapshot, SymbolCandidate candidate, string newName, bool renameFile, string root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidate);

        var symbol = Target(candidate.Symbol);
        var oldName = symbol.Name;
        var display = symbol.ToDisplayString(SymbolFormatter.Signature);
        newName = (newName ?? string.Empty).Trim();
        ValidateName(snapshot.Solution, symbol, newName);
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"{display} is already named '{newName}'.", "Pass the new name.");
        }

        var family = await FamilyAsync(snapshot.Solution, symbol, cancellationToken);
        var existing = family.SelectMany(member => ExistingDeclarations(member, newName, family)).Distinct(SymbolEqualityComparer.Default).ToList();
        if (existing.Count > 0)
        {
            throw new FarolException(
                ErrorCodes.Conflict,
                $"'{newName}' already exists where {display} is declared: {string.Join("; ", existing.Take(MaxConflictsShown).Select(s => $"{SymbolFormatter.Display(s)} at {SymbolFormatter.Location(s, root)}"))}. Nothing was written.",
                "Pick another name, or rename or remove the existing declaration first.");
        }

        var solution = snapshot.Solution;
        var options = new SymbolRenameOptions(RenameOverloads: false, RenameInStrings: false, RenameInComments: false, RenameFile: false);
        var renamed = await Renamer.RenameSymbolAsync(solution, symbol, options, newName, cancellationToken);

        var broken = await RebindingProblemsAsync(solution, renamed, symbol, oldName, newName, root, cancellationToken);
        if (broken.Count > 0)
        {
            throw new FarolException(
                ErrorCodes.Conflict,
                $"Renaming {display} to '{newName}' would change what {broken.Count} reference(s) mean: {string.Join("; ", broken.Take(MaxConflictsShown))}{(broken.Count > MaxConflictsShown ? $"; and {broken.Count - MaxConflictsShown} more" : string.Empty)}. Nothing was written.",
                "Pick another name: the new one clashes with a declaration these references can also see.");
        }

        var notes = new List<string>();
        var code = await CodeActionEditor.ComputeAsync(snapshot, renamed, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        var files = new Dictionary<string, WorkingFile>(StringComparer.OrdinalIgnoreCase);
        var generated = 0;
        foreach (var edit in code.Files)
        {
            if (IsGenerated(snapshot, edit.FilePath))
            {
                generated++;
                continue;
            }

            files[edit.FilePath] = new WorkingFile(edit.OldText, edit.NewText, edit.Encoding, edit.Project, edit.InSdkStyleProject);
        }

        if (generated > 0)
        {
            notes.Add($"{generated} generated file(s) under obj/ left as is: the next build regenerates them");
        }

        await RenameInMarkupAsync(snapshot, candidate, symbol, oldName, newName, files, notes, root, cancellationToken);

        var moves = renameFile && symbol is INamedTypeSymbol { ContainingType: null } type
            ? MoveFiles(snapshot, type, oldName, newName, files, notes, root)
            : [];

        var edits = files
            .Where(f => !string.Equals(f.Value.OldText, f.Value.NewText, StringComparison.Ordinal))
            .Select(f => new FileEdit(f.Key, f.Value.OldText, f.Value.NewText, f.Value.Encoding, f.Value.Project, f.Value.InSdkStyleProject, f.Value.ProjectFileUpdated))
            .OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new RenameResult(oldName, newName, display, new CodeActionChanges(edits, code.NotApplied), moves, notes);
    }

    // A constructor or destructor is renamed through its type, as the language ties their names together.
    private static ISymbol Target(ISymbol symbol)
    {
        var target = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor } method ? method.ContainingType : symbol;
        if (!target.Locations.Any(l => l.IsInSource))
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"{SymbolFormatter.Display(target)} is declared in {target.ContainingAssembly?.Name ?? "a referenced assembly"}, not in the workspace's source, so Farol cannot rename it.",
                "Rename a symbol declared in the solution.");
        }

        var supported = target switch
        {
            INamedTypeSymbol { TypeKind: not TypeKind.Error } => true,
            IMethodSymbol { MethodKind: MethodKind.Ordinary } => true,
            IPropertySymbol { IsIndexer: false } => true,
            IFieldSymbol or IEventSymbol or IParameterSymbol or ILocalSymbol or ITypeParameterSymbol => true,
            _ => false,
        };
        return supported
            ? target
            : throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"Farol cannot rename {SymbolFormatter.Display(target)}: it renames types, methods, properties, fields, events, parameters and locals.",
                target is INamespaceSymbol ? "dotnet_move_type moves types to another namespace." : "Rename it by hand.");
    }

    internal static void ValidateName(Solution solution, ISymbol symbol, string newName)
    {
        var languages = solution.Projects.Select(p => p.Language).Append(symbol.Language).ToHashSet(StringComparer.Ordinal);
        var problem =
            !CSharpFacts.IsValidIdentifier(newName) || (languages.Contains(LanguageNames.VisualBasic) && !VisualBasicFacts.IsValidIdentifier(newName)) ? "is not a valid identifier"
            : languages.Contains(LanguageNames.CSharp) && CSharpFacts.GetKeywordKind(newName) != CSharpKind.None ? "is a C# keyword"
            : languages.Contains(LanguageNames.VisualBasic) && VisualBasicFacts.GetKeywordKind(newName) is var kind && kind != VisualBasicKind.None && VisualBasicFacts.IsReservedKeyword(kind) ? "is a Visual Basic keyword"
            : null;
        if (problem is not null)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"'{newName}' {problem}.", "Pass a plain name such as CalculateTotal: letters, digits and underscores, not starting with a digit.");
        }
    }

    /// <summary>
    /// The symbol and the members Renamer renames with it: overrides, implementations, and the members it overrides or
    /// implements, when they are in source.
    /// </summary>
    private static async Task<List<ISymbol>> FamilyAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken)
    {
        var family = new List<ISymbol> { symbol };
        if (symbol is not (IMethodSymbol or IPropertySymbol or IEventSymbol) || symbol.ContainingType is not { } type)
        {
            return family;
        }

        family.AddRange(await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancellationToken));
        if (type.TypeKind == TypeKind.Interface)
        {
            family.AddRange(await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken));
        }

        for (var overridden = Overridden(symbol); overridden is not null; overridden = Overridden(overridden))
        {
            family.Add(overridden);
        }

        family.AddRange(type.AllInterfaces
            .SelectMany(i => i.GetMembers(symbol.Name))
            .Where(m => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(m), symbol)));
        return [.. family.Where(s => s.Locations.Any(l => l.IsInSource)).Select(s => s.OriginalDefinition).Distinct(SymbolEqualityComparer.Default)];
    }

    private static ISymbol? Overridden(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null,
    };

    // Declarations of the new name where the symbol is declared: a type or namespace beside a type, any member of the type
    // or its base types beside a member (an overload too: it could capture calls), a parameter beside a parameter. The
    // family renamed together is not a conflict.
    private static List<ISymbol> ExistingDeclarations(ISymbol symbol, string newName, IReadOnlyCollection<ISymbol> family)
    {
        var comparison = symbol.Language == LanguageNames.VisualBasic ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Named(ISymbol s) => string.Equals(s.Name, newName, comparison) && !family.Contains(s.OriginalDefinition, SymbolEqualityComparer.Default);

        switch (symbol)
        {
            case INamedTypeSymbol type:
                INamespaceOrTypeSymbol container = (INamespaceOrTypeSymbol?)type.ContainingType ?? type.ContainingNamespace;
                return [.. container.GetMembers().Where(Named).Where(m => m is not INamedTypeSymbol other || other.Arity == type.Arity)];
            case IParameterSymbol parameter:
                return parameter.ContainingSymbol is IMethodSymbol owner ? [.. owner.Parameters.Where(Named)] : [];
            case ILocalSymbol or ITypeParameterSymbol:
                return [];
            default:
                var found = new List<ISymbol>();
                for (var current = symbol.ContainingType; current is not null; current = current.BaseType)
                {
                    found.AddRange(current.GetMembers().Where(Named).Where(m => !m.IsImplicitlyDeclared));
                }

                return found;
        }
    }

    /// <summary>
    /// Every reference found before the rename must, in the renamed solution, be spelled with the new name and bind to
    /// the renamed declaration (an overload or an inherited member of that name could capture it). Returns the ones that do not.
    /// </summary>
    private static async Task<List<string>> RebindingProblemsAsync(Solution before, Solution after, ISymbol symbol, string oldName, string newName, string root, CancellationToken cancellationToken)
    {
        var comparison = symbol.Language == LanguageNames.VisualBasic ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var problems = new List<string>();
        var maps = new Dictionary<DocumentId, IReadOnlyList<TextChange>>();
        foreach (var group in await SymbolFinder.FindReferencesAsync(symbol, before, cancellationToken))
        {
            // What the references must bind to: the renamed declaration, found where the old one was.
            var declaration = group.Definition.Locations.FirstOrDefault(l => l.IsInSource);
            if (declaration is null || before.GetDocument(declaration.SourceTree) is not { } declaringDocument
                || await DeclaredAsync(after, declaringDocument, declaration.SourceSpan.Start, newName, comparison, maps, cancellationToken) is not { } renamedDeclaration)
            {
                continue;
            }

            var expected = SymbolFormatter.Id(renamedDeclaration.OriginalDefinition);

            // `new Old(...)` names the type: a constructor's references bind there.
            var alsoExpected = renamedDeclaration is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor ? SymbolFormatter.Id(constructor.ContainingType.OriginalDefinition) : expected;
            foreach (var reference in group.Locations.Where(l => !l.IsImplicit && !l.IsCandidateLocation))
            {
                var oldDocument = reference.Document;
                var start = reference.Location.SourceSpan.Start;
                var oldRoot = await oldDocument.GetSyntaxRootAsync(cancellationToken);
                if (oldRoot is null || !string.Equals(oldRoot.FindToken(start).ValueText, oldName, comparison))
                {
                    // An alias or an attribute's short name: not spelled with the old name, so not renamed either.
                    continue;
                }

                var where = DisplayPath.Location(root, oldDocument.FilePath ?? oldDocument.Name, reference.Location.GetLineSpan().StartLinePosition.Line + 1);
                var (token, model) = await TokenAsync(after, oldDocument, start, newName, comparison, maps, cancellationToken);
                if (model is null || !string.Equals(token.ValueText, newName, comparison))
                {
                    problems.Add($"{where} is not renamed");
                    continue;
                }

                var bound = Bound(model, token, cancellationToken);
                var id = bound is null ? null : SymbolFormatter.Id((bound is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : bound).OriginalDefinition);
                if (!string.Equals(id, expected, StringComparison.Ordinal) && !string.Equals(id, alsoExpected, StringComparison.Ordinal))
                {
                    problems.Add(bound is null ? $"{where} would no longer compile" : $"{where} would refer to {SymbolFormatter.Display(bound)}");
                }
            }
        }

        return problems;
    }

    private static async Task<ISymbol?> DeclaredAsync(Solution after, Document before, int position, string newName, StringComparison comparison, Dictionary<DocumentId, IReadOnlyList<TextChange>> maps, CancellationToken cancellationToken)
    {
        var (token, model) = await TokenAsync(after, before, position, newName, comparison, maps, cancellationToken);
        for (var node = token.Parent; model is not null && node is not null; node = node.Parent)
        {
            if (model.GetDeclaredSymbol(node, cancellationToken) is { } declared)
            {
                return declared;
            }
        }

        return null;
    }

    private static async Task<(SyntaxToken Token, SemanticModel? Model)> TokenAsync(
        Solution after, Document before, int position, string newName, StringComparison comparison, Dictionary<DocumentId, IReadOnlyList<TextChange>> maps, CancellationToken cancellationToken)
    {
        if (after.GetDocument(before.Id) is not { } document || await document.GetSyntaxRootAsync(cancellationToken) is not { } newRoot)
        {
            return (default, null);
        }

        if (!maps.TryGetValue(before.Id, out var changes))
        {
            changes = [.. (await document.GetTextChangesAsync(before, cancellationToken)).OrderBy(c => c.Span.Start)];
            maps[before.Id] = changes;
        }

        // The position moves by what changed before it. A change at the position itself is this token's rename, which
        // Renamer may have written with a qualifier (this.New, Ns.New): the new name is the identifier inside it.
        var shifted = position + changes.Where(c => c.Span.End <= position && c.Span.Start < position).Sum(c => (c.NewText?.Length ?? 0) - c.Span.Length);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (changes.FirstOrDefault(c => c.Span.Start <= position && position < Math.Max(c.Span.End, c.Span.Start + 1)) is { NewText: { Length: > 0 } written } covering)
        {
            var start = covering.Span.Start + changes.Where(c => c.Span.End <= covering.Span.Start && c.Span.Start < covering.Span.Start).Sum(c => (c.NewText?.Length ?? 0) - c.Span.Length);
            var renamed = newRoot.DescendantTokens(new TextSpan(start, written.Length)).FirstOrDefault(t => string.Equals(t.ValueText, newName, comparison));
            if (renamed != default)
            {
                return (renamed, model);
            }
        }

        return (newRoot.FindToken(shifted), model);
    }

    // The token's own node, then its parent (a generic name, a qualified name) while that starts at the token.
    private static ISymbol? Bound(SemanticModel model, SyntaxToken token, CancellationToken cancellationToken)
    {
        for (var node = token.Parent; node is not null && (node == token.Parent || node.SpanStart == token.SpanStart); node = node.Parent)
        {
            var info = model.GetSymbolInfo(node, cancellationToken);
            if ((info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null)) is { } symbol)
            {
                return symbol;
            }

            if (model.GetDeclaredSymbol(node, cancellationToken) is { } declared)
            {
                return declared;
            }
        }

        return null;
    }

    private static bool IsGenerated(WorkspaceSnapshot snapshot, string path)
    {
        var projects = snapshot.Projects.ProjectsContaining(path);
        var directory = projects.Count > 0 && snapshot.Projects.Get(projects[0]) is { } project ? Path.GetDirectoryName(project.FilePath)! : Path.GetDirectoryName(path)!;
        var relative = Path.GetRelativePath(directory, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).SkipLast(1).Any(s => s.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Markup names code by name: an event handler, a control, a code-behind or element type. Each reference the markup
    /// index resolved to the symbol has the old name on its line; that occurrence is replaced.
    /// </summary>
    private static async Task RenameInMarkupAsync(
        WorkspaceSnapshot snapshot, SymbolCandidate candidate, ISymbol symbol, string oldName, string newName, Dictionary<string, WorkingFile> files, List<string> notes, string root, CancellationToken cancellationToken)
    {
        var index = await MarkupIndex.GetAsync(snapshot, cancellationToken);
        var references = candidate.Variants
            .Select(v => SymbolFormatter.Id(Target(v.Symbol)))
            .Append(SymbolFormatter.Id(symbol))
            .Distinct(StringComparer.Ordinal)
            .SelectMany(index.ReferencesTo)
            .DistinctBy(r => (r.FilePath, r.Line, r.Column))
            .GroupBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase);
        foreach (var file in references)
        {
            var working = await ReadAsync(files, file.Key, file.First().Project, cancellationToken);
            var text = working.NewText!;
            var starts = LineStarts(text);
            var replacements = new SortedSet<int>();
            foreach (var reference in file.OrderBy(r => r.Line))
            {
                if (reference.Detail == "auto-wired event")
                {
                    notes.Add($"{DisplayPath.From(root, file.Key)}: the page wires {oldName} by name (AutoEventWireup); wire the renamed handler by hand");
                    continue;
                }

                if (Occurrence(text, starts, reference.Line, reference.Column, oldName) is { } offset)
                {
                    replacements.Add(offset);
                }
                else
                {
                    notes.Add($"{DisplayPath.Location(root, file.Key, reference.Line)}: the markup reference ({reference.Detail}) was left as is: '{oldName}' is not on that line, or is there more than once");
                }
            }

            var builder = new StringBuilder(text);
            foreach (var offset in replacements.Reverse())
            {
                builder.Remove(offset, oldName.Length).Insert(offset, newName);
            }

            files[file.Key] = working with { NewText = builder.ToString() };
        }
    }

    // The old name as a whole identifier on the line: from the reference's column first, otherwise the line's only one.
    private static int? Occurrence(string text, List<int> starts, int line, int column, string name)
    {
        if (line < 1 || line > starts.Count)
        {
            return null;
        }

        var lineStart = starts[line - 1];
        var lineEnd = line < starts.Count ? starts[line] : text.Length;
        var from = Math.Clamp(lineStart + column - 1, lineStart, lineEnd);
        if (Find(text, name, from, lineEnd) is { } atColumn)
        {
            return atColumn;
        }

        var first = Find(text, name, lineStart, lineEnd);
        return first is { } only && Find(text, name, only + 1, lineEnd) is null ? only : null;
    }

    private static int? Find(string text, string name, int from, int end)
    {
        for (var at = text.IndexOf(name, from, StringComparison.Ordinal); at >= 0 && at + name.Length <= end; at = text.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 ? ' ' : text[at - 1];
            var after = at + name.Length >= text.Length ? ' ' : text[at + name.Length];
            if (!IsIdentifierChar(before) && !IsIdentifierChar(after))
            {
                return at;
            }
        }

        return null;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    /// <summary>
    /// The files named after the type (Old.cs, Old.Designer.cs, Old.xaml.cs) become New.*, with their companions
    /// (Old.xaml, Old.resx), the project file items that list them, and XAML that names a moved XAML file. Web pages keep
    /// their names: renaming one changes its URL.
    /// </summary>
    private static List<SourceMove> MoveFiles(WorkspaceSnapshot snapshot, INamedTypeSymbol type, string oldName, string newName, Dictionary<string, WorkingFile> files, List<string> notes, string root)
    {
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("the files keep their names: the new name differs from the old one only in case");
            return [];
        }

        var planned = new List<(string From, string To, bool Source)>();
        foreach (var path in type.DeclaringSyntaxReferences.Select(r => r.SyntaxTree.FilePath).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsGenerated(snapshot, path))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(path)!;
            var name = Path.GetFileName(path);
            if (!name.StartsWith(oldName + ".", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!snapshot.Solution.GetDocumentIdsWithFilePath(path)
                .Select(id => snapshot.Projects.Get(id.ProjectId)?.FilePath)
                .All(project => project is not null && path.StartsWith(Path.GetDirectoryName(project) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                notes.Add($"{DisplayPath.From(root, path)} keeps its name: a project links it from outside its folder");
                continue;
            }

            var rest = name[oldName.Length..];
            var inner = Path.GetFileNameWithoutExtension(rest);
            if (WebMarkupExtensions.Any(e => inner.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                notes.Add($"{DisplayPath.From(root, path)} keeps its name: renaming the page or service would change its URL");
                continue;
            }

            planned.Add((path, Path.Combine(directory, newName + rest), true));
            var companions = inner.Length > 0 ? [inner] : CompanionExtensions.Where(e => e != ".xaml");
            foreach (var extension in companions)
            {
                var companion = Path.Combine(directory, oldName + extension);
                if (File.Exists(companion) && !planned.Any(p => p.From.Equals(companion, StringComparison.OrdinalIgnoreCase)))
                {
                    planned.Add((companion, Path.Combine(directory, newName + extension), false));
                }
            }
        }

        if (planned.Count == 0)
        {
            return [];
        }

        foreach (var (_, to, _) in planned)
        {
            if (File.Exists(to))
            {
                throw new FarolException(ErrorCodes.Conflict, $"'{DisplayPath.From(root, to)}' already exists, so the file cannot be renamed. Nothing was written.", "Call again with renameFile=false, or move the existing file first.");
            }
        }

        // Project files: the items that name the moved files. A classic project must list a moved source, or it would drop out of the build.
        var projectFiles = planned
            .SelectMany(p => snapshot.Projects.ProjectsContaining(p.From).Select(id => snapshot.Projects.Get(id)).OfType<ProjectEntry>())
            .DistinctBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var updatedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projectFiles)
        {
            var working = Read(files, project.FilePath, project.Name, project.IsSdkStyle);
            var text = working.NewText!;
            var projectDirectory = Path.GetDirectoryName(project.FilePath)!;
            foreach (var (from, to, source) in planned.Where(p => p.From.StartsWith(projectDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                var oldRelative = Path.GetRelativePath(projectDirectory, from);
                var newRelative = Path.GetRelativePath(projectDirectory, to);
                var replaced = ItemPath(oldRelative).Replace(text, m => m.Groups["head"].Value + newRelative.Replace('/', '\\') + "\"");
                replaced = WithDependentUpon(replaced, oldRelative, Path.GetFileName(from), Path.GetFileName(to));
                if (!project.IsSdkStyle && !ItemPath(oldRelative).IsMatch(text))
                {
                    throw new FarolException(
                        ErrorCodes.InvalidArgument,
                        $"{project.Name} is a classic project that does not list '{oldRelative}' by name, so Farol cannot rename the {(source ? "file" : "file with its companion")}. Nothing was written.",
                        "Call again with renameFile=false, then rename the file and its project item by hand.");
                }

                text = replaced;
            }

            if (!string.Equals(text, working.NewText, StringComparison.Ordinal))
            {
                files[project.FilePath] = working with { NewText = text };
                updatedProjects.Add(project.FilePath);
            }
        }

        // XAML that names a moved XAML file (StartupUri="MainWindow.xaml", a resource dictionary's Source).
        foreach (var (from, to, _) in planned.Where(p => MarkupFiles.IsXaml(p.From)))
        {
            var projectDirectory = snapshot.Projects.ProjectsContaining(from) is [var id, ..] && snapshot.Projects.Get(id) is { } owner ? Path.GetDirectoryName(owner.FilePath)! : Path.GetDirectoryName(from)!;
            foreach (var xaml in MarkupFiles.Enumerate(projectDirectory).Where(MarkupFiles.IsXaml))
            {
                var working = Read(files, xaml, string.Empty, true);
                var xamlDirectory = Path.GetDirectoryName(xaml)!;
                var replaced = XamlFileReference(Path.GetFileName(from)).Replace(working.NewText!, m =>
                    string.Equals(XamlTarget(m.Groups["prefix"].Value, Path.GetFileName(from), xamlDirectory, projectDirectory), from, StringComparison.OrdinalIgnoreCase)
                        ? m.Groups["head"].Value + m.Groups["prefix"].Value + Path.GetFileName(to) + "\""
                        : m.Value);
                if (!string.Equals(replaced, working.NewText, StringComparison.Ordinal))
                {
                    files[xaml] = working with { NewText = replaced };
                    notes.Add($"{DisplayPath.From(root, xaml)} names {Path.GetFileName(from)}: updated to {Path.GetFileName(to)}");
                }
            }
        }

        var moves = new List<SourceMove>();
        foreach (var (from, to, _) in planned)
        {
            var projects = snapshot.Projects.ProjectsContaining(from).Select(id => snapshot.Projects.Get(id)).OfType<ProjectEntry>().ToList();
            var project = projects.FirstOrDefault();
            var working = files.TryGetValue(from, out var known) ? known : Read(files, from, project?.Name ?? string.Empty, project?.IsSdkStyle ?? true);
            var listed = project is not null && updatedProjects.Contains(project.FilePath);
            files[from] = working with { NewText = null, ProjectFileUpdated = listed };
            files[to] = new WorkingFile(null, working.NewText, working.Encoding, working.Project, working.InSdkStyleProject, listed);
            moves.Add(new SourceMove(from, to));
        }

        return moves;
    }

    // Include="Orders\Old.cs" (also Update and Remove, with either slash).
    private static Regex ItemPath(string relative) =>
        new($"(?<head>\\b(?:Include|Update|Remove)\\s*=\\s*\"){Regex.Escape(relative).Replace("\\\\", "[\\\\/]")}\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Regex DependentUpon(string fileName) =>
        new($"(?<head><DependentUpon>\\s*){Regex.Escape(fileName)}(?<tail>\\s*</DependentUpon>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // An item element with a body: <Compile Include="Views\Old.Designer.cs"> ... </Compile>.
    private static readonly Regex ItemElement = new(
        "<(?<tag>[A-Za-z][\\w.]*)\\b[^>]*?\\b(?:Include|Update)\\s*=\\s*\"(?<path>[^\"]*)\"[^>]*(?<!/)>(?<body>.*?)</\\k<tag>\\s*>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    // <DependentUpon> names a file in the item's own folder: only items in the moved file's folder are rewritten.
    private static string WithDependentUpon(string text, string oldRelative, string fromName, string toName)
    {
        var folder = Folder(oldRelative);
        var dependent = DependentUpon(fromName);
        return ItemElement.Replace(text, m =>
            !string.Equals(Folder(m.Groups["path"].Value), folder, StringComparison.OrdinalIgnoreCase)
                ? m.Value
                : m.Value[..(m.Groups["body"].Index - m.Index)]
                    + dependent.Replace(m.Groups["body"].Value, d => d.Groups["head"].Value + toName + d.Groups["tail"].Value)
                    + m.Value[(m.Groups["body"].Index - m.Index + m.Groups["body"].Length)..]);
    }

    private static string Folder(string relative) => Path.GetDirectoryName(relative.Replace('/', '\\').Replace('\\', Path.DirectorySeparatorChar)) ?? string.Empty;

    private static Regex XamlFileReference(string fileName) =>
        new($"(?<head>=\\s*\")(?<prefix>(?:[^\"]*[/\\\\])?){Regex.Escape(fileName)}\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The file a XAML reference names: relative to the XAML file, or to the project with a leading slash or a pack URI.
    private static string XamlTarget(string prefix, string fileName, string xamlDirectory, string projectDirectory)
    {
        var pack = prefix.IndexOf(",,,", StringComparison.Ordinal);
        var relative = (pack >= 0 ? prefix[(pack + 3)..] : prefix).Replace('\\', '/');
        var fromProject = pack >= 0 || relative.StartsWith('/');
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            return Path.GetFullPath(Path.Combine([fromProject ? projectDirectory : xamlDirectory, .. parts, fileName]));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private static async Task<WorkingFile> ReadAsync(Dictionary<string, WorkingFile> files, string path, string project, CancellationToken cancellationToken)
    {
        if (files.TryGetValue(path, out var known))
        {
            return known;
        }

        using var reader = new StreamReader(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return new WorkingFile(text, text, reader.CurrentEncoding, project, true);
    }

    private static WorkingFile Read(Dictionary<string, WorkingFile> files, string path, string project, bool sdkStyle)
    {
        if (files.TryGetValue(path, out var known))
        {
            return known;
        }

        using var reader = new StreamReader(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return new WorkingFile(text, text, reader.CurrentEncoding, project, sdkStyle);
    }

    private sealed record WorkingFile(string? OldText, string? NewText, Encoding Encoding, string Project, bool InSdkStyleProject, bool ProjectFileUpdated = false);
}

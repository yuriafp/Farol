using System.Text;
using System.Text.RegularExpressions;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Engine.CodeActions;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using CS = Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpFacts = Microsoft.CodeAnalysis.CSharp.SyntaxFacts;
using CSharpSyntaxFactory = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using VB = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using VisualBasicFacts = Microsoft.CodeAnalysis.VisualBasic.SyntaxFacts;
using VisualBasicSyntaxFactory = Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory;

namespace Farol.Engine.Refactoring;

/// <summary>
/// Move type on public APIs (Roslyn offers "move to namespace" only through a dialog): the type into another namespace
/// and/or file of its project, the files it leaves or creates (with the project file items of classic projects), and
/// at every use site the type written with its new namespace, then imported and simplified by Roslyn's
/// <see cref="ImportAdder"/> and <see cref="Simplifier"/>.
/// </summary>
public static class MoveTypeRefactoring
{
    private static readonly SyntaxAnnotation Moved = new("Farol.MovedType");

    public static async Task<RefactoringResult> ComputeAsync(WorkspaceSnapshot snapshot, ISymbol symbol, string? toFile, string? toNamespace, string root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(symbol);

        var solution = snapshot.Solution;
        var type = Target(symbol);
        var display = SymbolFormatter.Display(type);
        var visualBasic = type.Language == LanguageNames.VisualBasic;
        var reference = type.DeclaringSyntaxReferences[0];
        var source = solution.GetDocument(reference.SyntaxTree)
            ?? throw new FarolException(ErrorCodes.InvalidArgument, $"{display} is not declared in a document of the workspace.");
        var sourcePath = source.FilePath!;
        var project = snapshot.Projects.Get(source.Project.Id)
            ?? throw new FarolException(ErrorCodes.InvalidArgument, $"{display} is not declared in a project of the workspace.");
        var projectDirectory = Path.GetDirectoryName(project.FilePath)!;

        var oldNamespace = type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString();
        var newNamespace = string.IsNullOrWhiteSpace(toNamespace) ? oldNamespace : toNamespace.Trim();
        ValidateNamespace(newNamespace, visualBasic);
        var targetPath = string.IsNullOrWhiteSpace(toFile) ? sourcePath : Path.GetFullPath(toFile);
        var names = visualBasic ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var namespaceChanges = !string.Equals(oldNamespace, newNamespace, names);
        var fileChanges = !string.Equals(targetPath, sourcePath, StringComparison.OrdinalIgnoreCase);
        if (!namespaceChanges && !fileChanges)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"{display} is already in namespace {Show(oldNamespace)} and in {DisplayPath.From(root, sourcePath)}.", "Pass toNamespace, toFile, or both.");
        }

        var target = fileChanges ? CheckTargetFile(solution, source, project, projectDirectory, targetPath, visualBasic, root) : source;
        var owners = snapshot.Projects.ProjectsContaining(sourcePath).Select(snapshot.Projects.Get).OfType<ProjectEntry>()
            .DistinctBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
        if (fileChanges && owners.Count > 1 && owners.Any(p => !p.IsSdkStyle))
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"{DisplayPath.From(root, sourcePath)} is compiled by {string.Join(", ", owners.Select(p => p.Name))}, and a classic project among them lists its files: Farol moves types out of a file one project owns.",
                "Move the type within its file (toNamespace only), or unlink the file first.");
        }
        if (namespaceChanges)
        {
            CheckFreeName(await source.Project.GetCompilationAsync(cancellationToken), type, newNamespace, root);
        }

        // 1. Use sites: the type written with its new namespace, for ImportAdder and Simplifier to tidy at the end.
        var declaration = await DeclarationAsync(reference, cancellationToken);
        var needed = namespaceChanges ? await NeededNamespacesAsync(source, declaration, type, oldNamespace, newNamespace, names, cancellationToken) : [];
        var extensionUsers = namespaceChanges ? await ExtensionUsersAsync(solution, type, source.Id, declaration.FullSpan, newNamespace, names, cancellationToken) : [];
        var touched = new HashSet<DocumentId>();
        // The declaration is marked first, while its span is the snapshot's: qualifying names above it moves it.
        var changed = await AnnotateAsync(solution, source.Id, declaration.FullSpan, cancellationToken);
        if (namespaceChanges)
        {
            changed = await QualifyUsesAsync(solution, changed, type, source.Id, declaration.FullSpan, newNamespace, visualBasic, touched, cancellationToken);
        }

        // 2. The declaration leaves its place.
        var notes = new List<string>();
        var sourceRoot = (await changed.GetDocument(source.Id)!.GetSyntaxRootAsync(cancellationToken))!;
        var moved = sourceRoot.GetAnnotatedNodes(Moved).Single();
        var container = NamespaceOf(moved);
        var alone = container is not null && Members(container).Count == 1 && container.Parent is CS.CompilationUnitSyntax or VB.CompilationUnitSyntax;
        var onlyType = TopLevelMembers(sourceRoot).Count == 1 && (alone || moved.Parent is CS.CompilationUnitSyntax or VB.CompilationUnitSyntax);
        var usings = Imports(sourceRoot, moved, needed, visualBasic);
        var lineBreak = (await source.GetTextAsync(cancellationToken)).ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var moves = new List<SourceMove>();
        var added = new List<(string Path, string ProjectFile)>();
        string? projectText = null;

        if (onlyType && (alone || !namespaceChanges) && (!fileChanges || !File.Exists(targetPath)))
        {
            // The whole file goes: its namespace is renamed in place, and it moves when a new file is asked for.
            var renamedRoot = namespaceChanges ? WithNamespace(sourceRoot, container!, newNamespace, source.Project, usings, visualBasic) : sourceRoot;
            changed = changed.WithDocumentSyntaxRoot(source.Id, renamedRoot);
            if (fileChanges)
            {
                moves.Add(new SourceMove(sourcePath, targetPath));
                projectText = ProjectItems.Move(project, projectDirectory, sourcePath, targetPath);
            }
        }
        else
        {
            var declarationText = Reindented(moved.ToFullString(), 0);
            if (moved.DescendantTokens().Any(t => t.Text.Contains('\n', StringComparison.Ordinal)))
            {
                notes.Add($"{type.Name} is re-indented in its new place: check its multi-line string literals, whose text moves with the lines");
            }

            var remaining = WithoutDeclaration(sourceRoot, moved);
            var nothingLeft = fileChanges && TopLevelMembers(remaining).Count == 0;
            if (nothingLeft)
            {
                // Nothing is left: the file goes, and the type joins the target file.
                foreach (var id in changed.GetDocumentIdsWithFilePath(sourcePath))
                {
                    changed = changed.RemoveDocument(id);
                }

                projectText = target is null ? null : ProjectItems.Remove(project, projectDirectory, sourcePath);
                notes.Add($"{DisplayPath.From(root, sourcePath)} held only {type.Name}: it is deleted");
            }
            else
            {
                changed = changed.WithDocumentSyntaxRoot(source.Id, remaining);
            }

            if (!fileChanges)
            {
                var sourceNow = (await changed.GetDocument(source.Id)!.GetSyntaxRootAsync(cancellationToken))!;
                changed = changed.WithDocumentSyntaxRoot(source.Id, Inserted(sourceNow, declarationText, newNamespace, source.Project, usings, lineBreak, visualBasic, root, sourcePath));
            }
            else if (target is not null)
            {
                var targetRoot = (await changed.GetDocument(target.Id)!.GetSyntaxRootAsync(cancellationToken))!;
                changed = changed.WithDocumentSyntaxRoot(target.Id, Inserted(targetRoot, declarationText, newNamespace, target.Project, usings, lineBreak, visualBasic, root, targetPath));
                touched.Add(target.Id);
            }
            else
            {
                var text = NewFile(declarationText, newNamespace, source.Project, usings, lineBreak, visualBasic, FileScoped(sourceRoot));
                var folders = Path.GetRelativePath(projectDirectory, Path.GetDirectoryName(targetPath)!)
                    .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                    .Where(f => f != ".")
                    .ToList();
                foreach (var id in snapshot.Projects.ProjectsContaining(sourcePath).DefaultIfEmpty(source.Project.Id).Distinct())
                {
                    changed = changed.AddDocument(DocumentId.CreateNewId(id), Path.GetFileName(targetPath), text, folders, targetPath);
                }

                if (!project.IsSdkStyle)
                {
                    projectText = ProjectItems.Add(project, projectDirectory, sourcePath, targetPath);
                    projectText = nothingLeft ? ProjectItems.Remove(project, projectDirectory, sourcePath, projectText) : projectText;
                    added.Add((targetPath, project.FilePath));
                }
            }
        }

        // 3. Callers of the type's extension methods import the new namespace; directives of an emptied namespace go.
        foreach (var id in extensionUsers.Where(id => changed.GetDocument(id) is not null))
        {
            var document = changed.GetDocument(id)!;
            var documentRoot = (await document.GetSyntaxRootAsync(cancellationToken))!;
            var breakText = (await document.GetTextAsync(cancellationToken)).ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            changed = changed.WithDocumentSyntaxRoot(id, WithImport(documentRoot, newNamespace, document.Project.Language == LanguageNames.VisualBasic, breakText));
            touched.Add(id);
        }

        if (namespaceChanges && oldNamespace.Length > 0)
        {
            var removed = await WithoutEmptiedImportsAsync(solution, changed, type, oldNamespace, touched, cancellationToken);
            changed = removed.Solution;
            if (removed.Count > 0)
            {
                notes.Add($"namespace {oldNamespace} has no types left: {removed.Count} using/Imports directive(s) of it are removed");
            }

            if (MarkupNaming(projectDirectory, $"{oldNamespace}.{type.Name}") is { Count: > 0 } markup)
            {
                notes.Add($"{string.Join(", ", markup.Select(f => DisplayPath.From(root, f)))} name {oldNamespace}.{type.Name} in markup or configuration, which Farol does not rewrite: change them to {newNamespace}.{type.Name}");
            }
        }

        // 4. Imports and simpler names where the type is used.
        touched.Add(source.Id);
        foreach (var id in touched.Where(id => changed.GetDocument(id) is not null))
        {
            var document = changed.GetDocument(id)!;
            document = await ImportAdder.AddImportsAsync(document, Simplifier.AddImportsAnnotation, cancellationToken: cancellationToken);
            document = await Simplifier.ReduceAsync(document, Simplifier.Annotation, cancellationToken: cancellationToken);
            document = await Formatter.FormatAsync(document, Formatter.Annotation, cancellationToken: cancellationToken);
            changed = document.Project.Solution;
        }

        var encoding = (await source.GetTextAsync(cancellationToken)).Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var computed = await CodeActionEditor.ComputeAsync(snapshot, changed, encoding, cancellationToken);
        var edits = Finish(computed.Files, moves, project, projectText, encoding);
        var title = $"move {display} to {(namespaceChanges ? $"namespace {Show(newNamespace)}" : string.Empty)}{(namespaceChanges && fileChanges ? " in " : string.Empty)}{(fileChanges ? DisplayPath.From(root, targetPath) : string.Empty)}";
        return new RefactoringResult(title, new CodeActionChanges(edits, computed.NotApplied), moves, notes) { Added = added };
    }

    private static INamedTypeSymbol Target(ISymbol symbol)
    {
        var type = symbol as INamedTypeSymbol ?? (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor ? constructor.ContainingType : null)
            ?? throw new FarolException(ErrorCodes.InvalidArgument, $"dotnet_move_type moves types; {SymbolFormatter.Display(symbol)} is not one.", "Pass a type, e.g. OrderCalculator.");
        type = type.OriginalDefinition;
        if (!type.Locations.Any(l => l.IsInSource))
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"{SymbolFormatter.Display(type)} is declared in {type.ContainingAssembly?.Name ?? "a referenced assembly"}, not in the workspace's source.");
        }

        if (type.ContainingType is { } outer)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"{SymbolFormatter.Display(type)} is nested in {SymbolFormatter.Display(outer)}: Farol moves top-level types.", $"Move {outer.Name} instead.");
        }

        if (type.DeclaringSyntaxReferences.Length != 1)
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"{SymbolFormatter.Display(type)} is partial, declared in {type.DeclaringSyntaxReferences.Length} places: Farol moves a type declared once.",
                "Move its parts by hand, or merge them first.");
        }

        return type;
    }

    private static void ValidateNamespace(string name, bool visualBasic)
    {
        var parts = name.Split('.');
        if (name.Length == 0 || parts.Any(p => !(visualBasic ? VisualBasicFacts.IsValidIdentifier(p) : CSharpFacts.IsValidIdentifier(p)) || (!visualBasic && CSharpFacts.GetKeywordKind(p) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None)))
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"'{name}' is not a namespace name.", "Pass a dotted name such as Legacy.Core.Billing; Farol does not move types to the global namespace.");
        }
    }

    private static string Show(string ns) => ns.Length == 0 ? "(global)" : ns;

    private static Document? CheckTargetFile(Solution solution, Document source, ProjectEntry project, string projectDirectory, string targetPath, bool visualBasic, string root)
    {
        var expected = visualBasic ? ".vb" : ".cs";
        if (!string.Equals(Path.GetExtension(targetPath), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"'{DisplayPath.From(root, targetPath)}' is not a {expected} file.", $"Pass a {expected} path in {project.Name}.");
        }

        if (!targetPath.StartsWith(projectDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"'{DisplayPath.From(root, targetPath)}' is outside {project.Name}'s folder: Farol moves a type within its project.",
                "Pass a file in the same project; moving a type to another project also changes the references between them.");
        }

        var existing = solution.GetDocumentIdsWithFilePath(targetPath);
        if (existing.Length > 0)
        {
            return existing.Contains(source.Id) ? source : solution.GetDocument(existing.FirstOrDefault(id => id.ProjectId == source.Project.Id) ?? existing[0]);
        }

        return File.Exists(targetPath)
            ? throw new FarolException(ErrorCodes.InvalidArgument, $"'{DisplayPath.From(root, targetPath)}' exists but is not compiled in {project.Name}.", "Pass a new file, or one of the project's source files.")
            : null;
    }

    private static void CheckFreeName(Compilation? compilation, INamedTypeSymbol type, string newNamespace, string root)
    {
        INamespaceSymbol? ns = compilation?.Assembly.GlobalNamespace;
        foreach (var part in newNamespace.Split('.'))
        {
            ns = ns?.GetNamespaceMembers().FirstOrDefault(n => string.Equals(n.Name, part, type.Language == LanguageNames.VisualBasic ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        }

        if (ns?.GetTypeMembers(type.Name, type.Arity).FirstOrDefault() is { } clash)
        {
            throw new FarolException(
                ErrorCodes.Conflict,
                $"Namespace {newNamespace} already has {SymbolFormatter.Display(clash)} at {SymbolFormatter.Location(clash, root)}. Nothing was written.",
                "Pick another namespace, or rename one of the types first (dotnet_rename).");
        }
    }

    private static async Task<SyntaxNode> DeclarationAsync(SyntaxReference reference, CancellationToken cancellationToken)
    {
        var node = await reference.GetSyntaxAsync(cancellationToken);
        return node switch
        {
            VB.TypeStatementSyntax { Parent: VB.TypeBlockSyntax block } => block,
            VB.EnumStatementSyntax { Parent: VB.EnumBlockSyntax block } => block,
            _ => node,
        };
    }

    private static async Task<Solution> AnnotateAsync(Solution solution, DocumentId id, Microsoft.CodeAnalysis.Text.TextSpan fullSpan, CancellationToken cancellationToken)
    {
        var root = (await solution.GetDocument(id)!.GetSyntaxRootAsync(cancellationToken))!;
        var node = root.DescendantNodes(fullSpan).First(n => n.FullSpan == fullSpan && n is CS.MemberDeclarationSyntax or VB.TypeBlockSyntax or VB.EnumBlockSyntax or VB.DelegateStatementSyntax);
        return solution.WithDocumentSyntaxRoot(id, root.ReplaceNode(node, node.WithAdditionalAnnotations(Moved)));
    }

    /// <summary>
    /// Every reference outside the declaration becomes global::New.Ns.Type (Global.New.Ns.Type in VB), marked for
    /// Simplifier and, outside using/Imports directives, for ImportAdder. The references are found on the snapshot's
    /// solution, whose text the marked solution shares, so their positions hold.
    /// </summary>
    private static async Task<Solution> QualifyUsesAsync(
        Solution original, Solution solution, INamedTypeSymbol type, DocumentId sourceId, Microsoft.CodeAnalysis.Text.TextSpan declarationSpan, string newNamespace, bool visualBasicType, HashSet<DocumentId> touched, CancellationToken cancellationToken)
    {
        var byDocument = (await SymbolFinder.FindReferencesAsync(type, original, cancellationToken))
            .Where(g => SymbolEqualityComparer.Default.Equals(g.Definition.OriginalDefinition, type) || g.Definition is IMethodSymbol { MethodKind: MethodKind.Constructor })
            .SelectMany(g => g.Locations)
            .Where(l => !l.IsImplicit && !l.IsCandidateLocation)
            .Where(l => !(l.Document.Id == sourceId && declarationSpan.Contains(l.Location.SourceSpan)))
            .GroupBy(l => l.Document.Id);
        _ = visualBasicType;
        foreach (var group in byDocument)
        {
            var document = solution.GetDocument(group.Key)!;
            var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
            // Found and bound on the snapshot's tree, then mapped by span onto the marked one, whose text is the same.
            var snapshotDocument = original.GetDocument(group.Key)!;
            var snapshotRoot = (await snapshotDocument.GetSyntaxRootAsync(cancellationToken))!;
            var model = (await snapshotDocument.GetSemanticModelAsync(cancellationToken))!;
            var visualBasic = document.Project.Language == LanguageNames.VisualBasic;
            var comparison = visualBasic ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var nodes = new HashSet<SyntaxNode>();
            foreach (var location in group)
            {
                var token = snapshotRoot.FindToken(location.Location.SourceSpan.Start, findInsideTrivia: true);
                if (token.Parent is null || token.Parent.IsPartOfStructuredTrivia() || !NamesType(token, type.Name, comparison))
                {
                    continue;
                }

                var node = Qualifiable(token.Parent, model, cancellationToken);
                if (root.DescendantNodesAndSelf(node.Span).FirstOrDefault(n => n.Span == node.Span && n.RawKind == node.RawKind) is { } mapped)
                {
                    nodes.Add(mapped);
                }
            }

            if (nodes.Count == 0)
            {
                continue;
            }

            // Outer nodes win: a qualified name inside another is rewritten with it.
            var outermost = nodes.Where(n => !nodes.Any(o => o != n && o.Span.Contains(n.Span))).ToList();
            var rewritten = root.ReplaceNodes(outermost, (original, _) => Qualified(original, newNamespace, visualBasic));
            solution = solution.WithDocumentSyntaxRoot(group.Key, rewritten);
            touched.Add(group.Key);
        }

        return solution;
    }

    // The type's name, or its attribute shorthand ([Audit] for AuditAttribute).
    private static bool NamesType(SyntaxToken token, string typeName, StringComparison comparison) =>
        string.Equals(token.ValueText, typeName, comparison)
        || (typeName.EndsWith("Attribute", comparison) && typeName.Length > "Attribute".Length
            && string.Equals(token.ValueText, typeName[..^"Attribute".Length], comparison)
            && token.Parent!.Ancestors().Any(a => a is CS.AttributeSyntax or VB.AttributeSyntax));

    /// <summary>
    /// The namespaces the type's body reached through its old namespace without importing them: the old namespace and
    /// its parents, for the types and extension methods it names there, unless the new namespace encloses them too.
    /// </summary>
    private static async Task<List<string>> NeededNamespacesAsync(Document source, SyntaxNode declaration, INamedTypeSymbol type, string oldNamespace, string newNamespace, StringComparison comparison, CancellationToken cancellationToken)
    {
        if (oldNamespace.Length == 0 || await source.GetSemanticModelAsync(cancellationToken) is not { } model)
        {
            return [];
        }

        bool Encloses(string outer, string inner) => string.Equals(outer, inner, comparison) || inner.StartsWith(outer + ".", comparison);
        var needed = new SortedSet<string>(comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var name in declaration.DescendantNodes().Where(n => n is CS.SimpleNameSyntax or VB.SimpleNameSyntax))
        {
            var qualified = name.Parent is CS.QualifiedNameSyntax { Right: var right } && right == name
                || name.Parent is CS.MemberAccessExpressionSyntax { Name: var csName } && csName == name
                || name.Parent is CS.AliasQualifiedNameSyntax
                || name.Parent is VB.QualifiedNameSyntax { Right: var vbRight } && vbRight == name
                || name.Parent is VB.MemberAccessExpressionSyntax { Name: var vbName } && vbName == name;
            var info = model.GetSymbolInfo(name, cancellationToken);
            var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
            INamespaceSymbol? ns = symbol switch
            {
                IMethodSymbol { ReducedFrom: { } extension } => extension.ContainingType.ContainingNamespace,
                INamedTypeSymbol named when !qualified => Outermost(named).ContainingNamespace,
                _ => null,
            };
            if (ns is null || ns.IsGlobalNamespace || (symbol is INamedTypeSymbol t && SymbolEqualityComparer.Default.Equals(Outermost(t).OriginalDefinition, type)))
            {
                continue;
            }

            var full = ns.ToDisplayString();
            if (Encloses(full, oldNamespace) && !Encloses(full, newNamespace))
            {
                needed.Add(full);
            }
        }

        return [.. needed];
    }

    private static INamedTypeSymbol Outermost(INamedTypeSymbol type)
    {
        while (type.ContainingType is { } outer)
        {
            type = outer;
        }

        return type;
    }

    /// <summary>The documents that call the type's extension methods as instance methods, from outside the new namespace: they need its import.</summary>
    private static async Task<List<DocumentId>> ExtensionUsersAsync(Solution solution, INamedTypeSymbol type, DocumentId sourceId, Microsoft.CodeAnalysis.Text.TextSpan declarationSpan, string newNamespace, StringComparison comparison, CancellationToken cancellationToken)
    {
        if (!type.MightContainExtensionMethods)
        {
            return [];
        }

        var users = new HashSet<DocumentId>();
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsExtensionMethod))
        {
            foreach (var location in (await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken)).SelectMany(g => g.Locations))
            {
                var document = location.Document;
                var start = location.Location.SourceSpan.Start;
                if (location.IsImplicit || location.IsCandidateLocation || users.Contains(document.Id) || (document.Id == sourceId && declarationSpan.Contains(start)))
                {
                    continue;
                }

                var model = (await document.GetSemanticModelAsync(cancellationToken))!;
                var token = (await document.GetSyntaxRootAsync(cancellationToken))!.FindToken(start);
                if (token.Parent is null || model.GetSymbolInfo(token.Parent, cancellationToken).Symbol is not IMethodSymbol { ReducedFrom: not null })
                {
                    continue;
                }

                var enclosing = model.GetEnclosingSymbol(start, cancellationToken);
                var ns = enclosing as INamespaceSymbol ?? enclosing?.ContainingNamespace;
                var name = ns is null || ns.IsGlobalNamespace ? string.Empty : ns.ToDisplayString();
                if (!string.Equals(name, newNamespace, comparison) && !name.StartsWith(newNamespace + ".", comparison))
                {
                    users.Add(document.Id);
                }
            }
        }

        return [.. users];
    }

    /// <summary>
    /// Removes the using/Imports directives of the old namespace when the move leaves it without members in the
    /// compilation that reads them (another project or assembly may still declare it).
    /// </summary>
    private static async Task<(Solution Solution, int Count)> WithoutEmptiedImportsAsync(Solution original, Solution changed, INamedTypeSymbol type, string oldNamespace, HashSet<DocumentId> touched, CancellationToken cancellationToken)
    {
        var count = 0;
        var emptied = new Dictionary<ProjectId, bool>();
        foreach (var document in changed.Projects.SelectMany(p => p.Documents).ToList())
        {
            var text = await document.GetTextAsync(cancellationToken);
            if (!text.ToString().Contains(oldNamespace, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var visualBasic = document.Project.Language == LanguageNames.VisualBasic;
            var comparison = visualBasic ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
            var directives = Directives(root, oldNamespace, comparison);
            if (directives.Count == 0)
            {
                continue;
            }

            if (!emptied.TryGetValue(document.Project.Id, out var isEmptied))
            {
                var compilation = original.GetProject(document.Project.Id) is { } project ? await project.GetCompilationAsync(cancellationToken) : null;
                emptied[document.Project.Id] = isEmptied = compilation is not null && Emptied(compilation, type, oldNamespace, comparison);
            }

            if (!isEmptied)
            {
                continue;
            }

            changed = changed.WithDocumentSyntaxRoot(document.Id, root.RemoveNodes(directives, SyntaxRemoveOptions.KeepLeadingTrivia)!);
            touched.Add(document.Id);
            count += directives.Count;
        }

        return (changed, count);
    }

    // using Old; / Imports Old (a whole statement, or one clause of several).
    private static List<SyntaxNode> Directives(SyntaxNode root, string ns, StringComparison comparison)
    {
        static string Plain(string name) => name.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("global::", string.Empty, StringComparison.Ordinal);
        if (root is CS.CompilationUnitSyntax unit)
        {
            return [.. unit.DescendantNodes(n => n is CS.CompilationUnitSyntax or CS.BaseNamespaceDeclarationSyntax).OfType<CS.UsingDirectiveSyntax>()
                .Where(u => u.Alias is null && u.StaticKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None) && u.Name is not null && string.Equals(Plain(u.Name.ToString()), ns, comparison))];
        }

        var result = new List<SyntaxNode>();
        foreach (var statement in ((VB.CompilationUnitSyntax)root).Imports)
        {
            var matching = statement.ImportsClauses.OfType<VB.SimpleImportsClauseSyntax>()
                .Where(c => c.Alias is null && string.Equals(c.Name.ToString().Replace("Global.", string.Empty, StringComparison.OrdinalIgnoreCase).Replace(" ", string.Empty, StringComparison.Ordinal), ns, comparison))
                .ToList();
            if (matching.Count > 0 && matching.Count == statement.ImportsClauses.Count)
            {
                result.Add(statement);
            }
            else
            {
                result.AddRange(matching);
            }
        }

        return result;
    }

    // Whether the namespace has nothing but the type, as the compilation sees it (its own and referenced assemblies).
    private static bool Emptied(Compilation compilation, INamedTypeSymbol type, string ns, StringComparison comparison)
    {
        INamespaceSymbol? current = compilation.GlobalNamespace;
        foreach (var part in ns.Split('.'))
        {
            current = current?.GetNamespaceMembers().FirstOrDefault(n => string.Equals(n.Name, part, comparison));
        }

        return current is not null && current.GetMembers().All(m => m is INamedTypeSymbol t && t.Name == type.Name && t.Arity == type.Arity && t.ContainingAssembly?.Name == type.ContainingAssembly?.Name);
    }

    // Adds using/Imports of the namespace in sorted place (System first when the file does so), unless it is there.
    private static SyntaxNode WithImport(SyntaxNode root, string ns, bool visualBasic, string lineBreak)
    {
        static (int, string) Key(string name) => (name == "System" || name.StartsWith("System.", StringComparison.Ordinal) ? 0 : 1, name);
        if (visualBasic)
        {
            var unit = (VB.CompilationUnitSyntax)root;
            if (Directives(unit, ns, StringComparison.OrdinalIgnoreCase).Count > 0)
            {
                return unit;
            }

            var statement = VisualBasicSyntaxFactory.ParseCompilationUnit($"Imports {ns}{lineBreak}").Imports[0].WithAdditionalAnnotations(Formatter.Annotation);
            var before = unit.Imports.FirstOrDefault(i => i.ImportsClauses.FirstOrDefault() is VB.SimpleImportsClauseSyntax { Alias: null } c && Key(c.Name.ToString()).CompareTo(Key(ns)) > 0);
            return before is null ? unit.AddImports(statement) : unit.WithImports(unit.Imports.Insert(unit.Imports.IndexOf(before), statement));
        }

        var cs = (CS.CompilationUnitSyntax)root;
        if (cs.Usings.Any(u => u.Alias is null && u.Name?.ToString() == ns))
        {
            return cs;
        }

        var directive = CSharpSyntaxFactory.ParseCompilationUnit($"using {ns};{lineBreak}").Usings[0].WithAdditionalAnnotations(Formatter.Annotation);
        var next = cs.Usings.FirstOrDefault(u => u.Alias is null && u.StaticKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None) && u.GlobalKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None) && u.Name is not null && Key(u.Name.ToString()).CompareTo(Key(ns)) > 0);
        return next is null ? cs.AddUsings(directive) : cs.WithUsings(cs.Usings.Insert(cs.Usings.IndexOf(next), directive));
    }

    // Markup and configuration files of the project folder that write the type's full name as text.
    private static List<string> MarkupNaming(string projectDirectory, string fullName)
    {
        string[] extensions = [".aspx", ".ascx", ".asax", ".master", ".ashx", ".svc", ".xaml", ".config"];
        try
        {
            return [.. Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
                .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Where(f => !Path.GetRelativePath(projectDirectory, f).Split(Path.DirectorySeparatorChar).SkipLast(1).Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                .Where(f => File.ReadAllText(f).Contains(fullName, StringComparison.OrdinalIgnoreCase))
                .Take(5)];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    // The name node, or the qualified name around it when its left side is the old namespace (Old.Ns.Type).
    private static SyntaxNode Qualifiable(SyntaxNode name, SemanticModel model, CancellationToken cancellationToken)
    {
        var parent = name.Parent;
        SyntaxNode? left = parent switch
        {
            CS.QualifiedNameSyntax q when q.Right == name => q.Left,
            CS.MemberAccessExpressionSyntax m when m.Name == name => m.Expression,
            CS.AliasQualifiedNameSyntax a when a.Name == name => a.Alias,
            VB.QualifiedNameSyntax q when q.Right == name => q.Left,
            VB.MemberAccessExpressionSyntax m when m.Name == name => m.Expression,
            _ => null,
        };
        return left is not null && model.GetSymbolInfo(left, cancellationToken).Symbol is INamespaceSymbol ? parent! : name;
    }

    private static SyntaxNode Qualified(SyntaxNode original, string newNamespace, bool visualBasic)
    {
        var simple = original switch
        {
            CS.QualifiedNameSyntax q => q.Right,
            CS.MemberAccessExpressionSyntax m => m.Name,
            CS.AliasQualifiedNameSyntax a => a.Name,
            VB.QualifiedNameSyntax q => q.Right,
            VB.MemberAccessExpressionSyntax m => m.Name,
            _ => original,
        };
        var inDirective = original.Ancestors().Any(a => a is CS.UsingDirectiveSyntax or VB.ImportsStatementSyntax);
        SyntaxNode replacement;
        if (visualBasic)
        {
            var text = $"Global.{newNamespace}.{simple.WithoutTrivia()}";
            replacement = original is VB.ExpressionSyntax expression && !VisualBasicFacts.IsInNamespaceOrTypeContext(expression)
                ? VisualBasicSyntaxFactory.ParseExpression(text)
                : VisualBasicSyntaxFactory.ParseName(text);
        }
        else
        {
            var text = $"global::{newNamespace}.{simple.WithoutTrivia()}";
            replacement = original is CS.ExpressionSyntax expression && !CSharpFacts.IsInNamespaceOrTypeContext(expression)
                ? CSharpSyntaxFactory.ParseExpression(text)
                : CSharpSyntaxFactory.ParseName(text);
        }

        replacement = replacement.WithTriviaFrom(original).WithAdditionalAnnotations(Simplifier.Annotation);
        return inDirective ? replacement : replacement.WithAdditionalAnnotations(Simplifier.AddImportsAnnotation);
    }

    private static SyntaxNode? NamespaceOf(SyntaxNode node) => node.Parent is CS.BaseNamespaceDeclarationSyntax or VB.NamespaceBlockSyntax ? node.Parent : null;

    private static List<SyntaxNode> Members(SyntaxNode container) => container switch
    {
        CS.BaseNamespaceDeclarationSyntax ns => [.. ns.Members],
        VB.NamespaceBlockSyntax ns => [.. ns.Members],
        CS.CompilationUnitSyntax unit => [.. unit.Members],
        VB.CompilationUnitSyntax unit => [.. unit.Members],
        _ => [],
    };

    // The types of a file, through its namespaces.
    private static List<SyntaxNode> TopLevelMembers(SyntaxNode root) =>
        [.. Members(root).SelectMany(m => m is CS.BaseNamespaceDeclarationSyntax or VB.NamespaceBlockSyntax ? TopLevelMembers(m) : [m])];

    private static bool FileScoped(SyntaxNode root) => root is CS.CompilationUnitSyntax unit && unit.Members.OfType<CS.FileScopedNamespaceDeclarationSyntax>().Any();

    /// <summary>The imports the type's new place needs: the declaring file's (not its global usings), and the namespaces its body reached through the old one.</summary>
    private static List<string> Imports(SyntaxNode root, SyntaxNode declaration, List<string> needed, bool visualBasic)
    {
        var imports = visualBasic
            ? ((VB.CompilationUnitSyntax)root).Imports.Select(i => i.ToString().Trim()).ToList()
            : [.. ((CS.CompilationUnitSyntax)root).Usings.Concat(declaration.Ancestors().OfType<CS.BaseNamespaceDeclarationSyntax>().SelectMany(n => n.Usings))
                .Where(u => u.GlobalKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None))
                .Select(u => u.ToString().Trim())];
        imports.AddRange(needed.Select(n => visualBasic ? $"Imports {n}" : $"using {n};"));
        return [.. imports.Distinct(visualBasic ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];
    }

    // How a namespace is written in a file of the project: VB writes it below the project's root namespace.
    private static string? Written(string fullName, Project project)
    {
        if (project.CompilationOptions is not Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions { RootNamespace: { Length: > 0 } rootNamespace })
        {
            return fullName;
        }

        return string.Equals(fullName, rootNamespace, StringComparison.OrdinalIgnoreCase) ? null
            : fullName.StartsWith(rootNamespace + ".", StringComparison.OrdinalIgnoreCase) ? fullName[(rootNamespace.Length + 1)..]
            : "Global." + fullName;
    }

    private static SyntaxNode WithNamespace(SyntaxNode root, SyntaxNode container, string newNamespace, Project project, List<string> usings, bool visualBasic)
    {
        SyntaxNode renamed;
        if (visualBasic)
        {
            var block = (VB.NamespaceBlockSyntax)container;
            var written = Written(newNamespace, project)
                ?? throw new FarolException(ErrorCodes.InvalidArgument, $"{newNamespace} is the project's root namespace: the file would need no Namespace block, which Farol does not write in place.", "Pass toFile too, so the type goes to a new file.");
            renamed = root.ReplaceNode(block.NamespaceStatement.Name, VisualBasicSyntaxFactory.ParseName(written).WithTriviaFrom(block.NamespaceStatement.Name));
        }
        else
        {
            var ns = (CS.BaseNamespaceDeclarationSyntax)container;
            renamed = root.ReplaceNode(ns.Name, CSharpSyntaxFactory.ParseName(newNamespace).WithTriviaFrom(ns.Name));
        }

        return WithImports(renamed, usings, visualBasic);
    }

    private static SyntaxNode WithImports(SyntaxNode root, IEnumerable<string> imports, bool visualBasic)
    {
        if (visualBasic)
        {
            var unit = (VB.CompilationUnitSyntax)root;
            var present = unit.Imports.Select(i => i.ToString().Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = imports.Where(i => !present.Contains(i)).Select(i => VisualBasicSyntaxFactory.ParseCompilationUnit(i + "\r\n").Imports[0]).ToArray();
            return missing.Length == 0 ? unit : unit.AddImports(missing);
        }

        var cs = (CS.CompilationUnitSyntax)root;
        var existing = cs.Usings.Select(u => u.ToString().Trim()).ToHashSet(StringComparer.Ordinal);
        var add = imports.Where(i => !existing.Contains(i)).Select(i => CSharpSyntaxFactory.ParseCompilationUnit(i + "\r\n").Usings[0]).ToArray();
        return add.Length == 0 ? cs : cs.AddUsings(add);
    }

    private static SyntaxNode WithoutDeclaration(SyntaxNode root, SyntaxNode declaration)
    {
        var container = NamespaceOf(declaration);
        var without = root.RemoveNode(declaration, SyntaxRemoveOptions.KeepNoTrivia)!;
        if (container is not null && Members(container).Count == 1)
        {
            var emptied = without.DescendantNodes().FirstOrDefault(n => n.SpanStart == container.SpanStart && n.GetType() == container.GetType());
            if (emptied is not null)
            {
                without = without.RemoveNode(emptied, SyntaxRemoveOptions.KeepNoTrivia)!;
            }
        }

        return without;
    }

    /// <summary>The declaration added to a file: inside its namespace block when the file has one, else in a new block at the end.</summary>
    private static SyntaxNode Inserted(SyntaxNode root, string declaration, string newNamespace, Project project, List<string> usings, string lineBreak, bool visualBasic, string displayRoot, string path)
    {
        root = WithImports(root, usings, visualBasic);
        if (visualBasic)
        {
            var unit = (VB.CompilationUnitSyntax)root;
            var written = Written(newNamespace, project);
            var block = written is null
                ? Indented(declaration, 0, lineBreak)
                : $"{lineBreak}Namespace {written}{lineBreak}{Indented(declaration, 4, lineBreak)}End Namespace{lineBreak}";
            var parsed = VisualBasicSyntaxFactory.ParseCompilationUnit(block);
            var existing = written is null ? null : unit.Members.OfType<VB.NamespaceBlockSyntax>().FirstOrDefault(n => string.Equals(n.NamespaceStatement.Name.ToString(), written, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var member = VisualBasicSyntaxFactory.ParseCompilationUnit(Indented(declaration, 4, lineBreak)).Members;
                return unit.ReplaceNode(existing, existing.AddMembers([.. member]));
            }

            return unit.AddMembers([.. parsed.Members]);
        }

        var cs = (CS.CompilationUnitSyntax)root;
        var target = cs.Members.OfType<CS.BaseNamespaceDeclarationSyntax>().FirstOrDefault(n => string.Equals(n.Name.ToString(), newNamespace, StringComparison.Ordinal));
        if (target is not null)
        {
            var indent = target is CS.FileScopedNamespaceDeclarationSyntax ? 0 : 4;
            var member = (CS.MemberDeclarationSyntax)CSharpSyntaxFactory.ParseCompilationUnit(lineBreak + Indented(declaration, indent, lineBreak)).Members[0];
            return cs.ReplaceNode(target, target.AddMembers(member));
        }

        if (FileScoped(cs))
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"{DisplayPath.From(displayRoot, path)} has a file-scoped namespace, so it cannot hold a type of namespace {newNamespace}.",
                "Pass toFile with a new file for the type.");
        }

        var namespaceBlock = $"{lineBreak}namespace {newNamespace}{lineBreak}{{{lineBreak}{Indented(declaration, 4, lineBreak)}}}{lineBreak}";
        return cs.AddMembers([.. CSharpSyntaxFactory.ParseCompilationUnit(namespaceBlock).Members]);
    }

    private static string NewFile(string declaration, string newNamespace, Project project, List<string> usings, string lineBreak, bool visualBasic, bool fileScoped)
    {
        var builder = new StringBuilder();
        foreach (var import in usings)
        {
            builder.Append(import).Append(lineBreak);
        }

        if (usings.Count > 0)
        {
            builder.Append(lineBreak);
        }

        if (visualBasic)
        {
            var written = Written(newNamespace, project);
            if (written is null)
            {
                builder.Append(Indented(declaration, 0, lineBreak));
            }
            else
            {
                builder.Append("Namespace ").Append(written).Append(lineBreak).Append(Indented(declaration, 4, lineBreak)).Append("End Namespace").Append(lineBreak);
            }
        }
        else if (fileScoped)
        {
            builder.Append("namespace ").Append(newNamespace).Append(';').Append(lineBreak).Append(lineBreak).Append(Indented(declaration, 0, lineBreak));
        }
        else
        {
            builder.Append("namespace ").Append(newNamespace).Append(lineBreak).Append('{').Append(lineBreak).Append(Indented(declaration, 4, lineBreak)).Append('}').Append(lineBreak);
        }

        return builder.ToString();
    }

    // The declaration's own indentation removed: lines start at column 0 relative to it.
    private static string Reindented(string text, int indent)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var first = 0;
        while (first < lines.Length && lines[first].Trim().Length == 0)
        {
            first++;
        }

        var last = lines.Length - 1;
        while (last >= first && lines[last].Trim().Length == 0)
        {
            last--;
        }

        var kept = lines[first..(last + 1)];
        var common = kept.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', kept.Select(l => l.Trim().Length == 0 ? string.Empty : new string(' ', indent) + l[Math.Min(common, l.Length)..]));
    }

    private static string Indented(string declaration, int indent, string lineBreak) =>
        string.Join(lineBreak, declaration.Split('\n').Select(l => l.Length == 0 ? l : new string(' ', indent) + l)) + lineBreak;

    /// <summary>The edits with the moved file split into a deletion and a creation, and the project file edit, for classic projects.</summary>
    private static List<FileEdit> Finish(IReadOnlyList<FileEdit> files, List<SourceMove> moves, ProjectEntry project, string? projectText, Encoding encoding)
    {
        var edits = files.ToDictionary(f => f.FilePath, StringComparer.OrdinalIgnoreCase);
        var listed = projectText is not null;
        foreach (var move in moves)
        {
            var edit = edits.TryGetValue(move.From, out var known) ? known : null;
            var text = edit?.NewText ?? File.ReadAllText(move.From);
            var oldText = edit?.OldText ?? text;
            edits[move.From] = new FileEdit(move.From, oldText, null, edit?.Encoding ?? encoding, project.Name, project.IsSdkStyle, listed);
            edits[move.To] = new FileEdit(move.To, null, text, edit?.Encoding ?? encoding, project.Name, project.IsSdkStyle, listed);
        }

        foreach (var (path, edit) in edits.ToList())
        {
            if ((edit.OldText is null || edit.NewText is null) && listed)
            {
                edits[path] = edit with { ProjectFileUpdated = true };
            }
        }

        if (projectText is not null)
        {
            var current = File.ReadAllText(project.FilePath);
            if (!string.Equals(current, projectText, StringComparison.Ordinal))
            {
                edits[project.FilePath] = new FileEdit(project.FilePath, current, projectText, encoding, project.Name, project.IsSdkStyle);
            }
        }

        return [.. edits.Values.OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Project file items for the files a move creates, renames or deletes: classic projects list each one; SDK-style ones glob.</summary>
    private static class ProjectItems
    {
        public static string? Move(ProjectEntry project, string directory, string from, string to)
        {
            if (project.IsSdkStyle)
            {
                return null;
            }

            var text = File.ReadAllText(project.FilePath);
            var item = ItemPath(Path.GetRelativePath(directory, from));
            return item.IsMatch(text)
                ? item.Replace(text, m => m.Groups["head"].Value + Path.GetRelativePath(directory, to) + "\"", 1)
                : throw Unlisted(project, directory, from);
        }

        public static string? Add(ProjectEntry project, string directory, string beside, string path, string? text = null)
        {
            text ??= File.ReadAllText(project.FilePath);
            var line = ItemLine(Path.GetRelativePath(directory, beside)).Match(text);
            if (!line.Success)
            {
                throw Unlisted(project, directory, beside);
            }

            var element = line.Value.TrimEnd('\r', '\n');
            var added = Regex.Replace(element, "(Include\\s*=\\s*\")[^\"]*\"", m => m.Groups[1].Value + Path.GetRelativePath(directory, path) + "\"");
            var lineBreak = line.Value.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            return text.Insert(line.Index + line.Length, added.TrimStart('\r', '\n') + lineBreak);
        }

        public static string? Remove(ProjectEntry project, string directory, string path, string? text = null)
        {
            if (project.IsSdkStyle)
            {
                return text;
            }

            text ??= File.ReadAllText(project.FilePath);
            var line = ItemLine(Path.GetRelativePath(directory, path)).Match(text);
            return line.Success ? text.Remove(line.Index, line.Length) : throw Unlisted(project, directory, path);
        }

        private static FarolException Unlisted(ProjectEntry project, string directory, string path) => new(
            ErrorCodes.InvalidArgument,
            $"{project.Name} is a classic project that does not list '{Path.GetRelativePath(directory, path)}' as a one-line <Compile Include=\"...\" /> item, so Farol cannot change its files. Nothing was written.",
            "Move the type within its file (toNamespace only), or edit the project file by hand.");

        private static Regex ItemPath(string relative) =>
            new($"(?<head>\\bInclude\\s*=\\s*\"){Regex.Escape(relative).Replace("\\\\", "[\\\\/]")}\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // A whole line holding a self-closing <Compile Include="..." /> of the file.
        private static Regex ItemLine(string relative) =>
            new($"(?m)^[ \\t]*<Compile\\s+Include\\s*=\\s*\"{Regex.Escape(relative).Replace("\\\\", "[\\\\/]")}\"\\s*/>[ \\t]*\\r?\\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

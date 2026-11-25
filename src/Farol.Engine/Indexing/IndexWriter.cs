using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Farol.Engine.Analysis;
using Farol.Engine.Markup;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Data.Sqlite;
using CSharpSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;
using VisualBasicSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Farol.Engine.Indexing;

/// <summary>
/// Writes a snapshot's index into a new SQLite file: the projects as dotnet_overview shows them, every declaration with
/// its ID and locations, every reference to a declaration (and every call, to members of referenced assemblies too) with
/// the member it is in and its one line of code, markup references, the type and member hierarchy, and each file's time
/// and size. File times and sizes are read before the code, so a file that changes meanwhile shows as changed.
/// </summary>
internal static class IndexWriter
{
    private const int MaxSnippetLength = ReferenceFinder.MaxSnippetLength;

    private static readonly string[] ReferenceParameters = ["$s", "$f", "$l", "$c", "$n", "$k", "$p", "$w", "$o"];

    public static async Task WriteAsync(WorkspaceSnapshot snapshot, SolutionOverview overview, string path, CancellationToken cancellationToken)
    {
        var solution = snapshot.Solution;
        var files = new FileTable();
        foreach (var document in solution.Projects.SelectMany(p => p.Documents).Select(d => d.FilePath).OfType<string>())
        {
            files.Add(document, IndexSchema.SourceFile);
        }

        foreach (var project in solution.Projects.Select(p => p.FilePath).OfType<string>().Append(snapshot.Target.Path))
        {
            files.Add(project, IndexSchema.ProjectFile);
        }

        foreach (var markup in snapshot.Projects.ProjectDirectories.SelectMany(MarkupFiles.Enumerate))
        {
            files.Add(markup, IndexSchema.MarkupFile);
        }

        foreach (var input in ProjectInputs(snapshot))
        {
            files.Add(input, IndexSchema.ProjectFile);
        }

        var symbols = await DeclarationsAsync(snapshot, files, cancellationToken);
        var labels = new LabelTable();

        using var connection = IndexSchema.Open(path, readOnly: false);
        using (var pragma = connection.CreateCommand())
        {
            // A temporary file written once and moved into place: no journal needed.
            pragma.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA page_size=8192;";
            pragma.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, IndexSchema.Create);
        await ReferencesAsync(snapshot, symbols, files, labels, connection, transaction, cancellationToken);
        MarkupReferences(await MarkupIndex.GetAsync(snapshot, cancellationToken), symbols, files, labels, connection, transaction);
        Relations(symbols, connection, transaction, cancellationToken);
        WriteSymbols(symbols, files, connection, transaction);
        files.Write(connection, transaction);
        labels.Write(connection, transaction);
        WriteMeta(connection, transaction, snapshot, overview);
        Execute(connection, transaction, IndexSchema.Indexes);
        transaction.Commit();
    }

    // The files besides project files that change what the projects are, as the watcher reloads on them: in each project's
    // folder and the folders above it up to the workspace's.
    private static readonly string[] ProjectInputNames =
    [
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "Directory.Build.rsp", "global.json", "nuget.config", "NuGet.Config", "packages.config",
    ];

    private static IEnumerable<string> ProjectInputs(WorkspaceSnapshot snapshot)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var top = Path.GetDirectoryName(snapshot.Target.Path);
        var folders = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var project in snapshot.Projects.ProjectDirectories)
        {
            for (var folder = project; folder is not null && folders.Add(folder); folder = Path.GetDirectoryName(folder))
            {
                if (top is null || string.Equals(folder, top, comparison) || !folder.StartsWith(top, comparison))
                {
                    break;
                }
            }
        }

        if (top is not null)
        {
            folders.Add(top);
        }

        // nuget.config and NuGet.Config are one file where names ignore case.
        return folders.SelectMany(f => ProjectInputNames.Select(n => Path.Combine(f, n))).Where(File.Exists).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void WriteMeta(SqliteConnection connection, SqliteTransaction transaction, WorkspaceSnapshot snapshot, SolutionOverview overview)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO meta(key, value) VALUES ($key, $value)";
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var value = command.Parameters.Add("$value", SqliteType.Text);
        void Put(string k, string v)
        {
            key.Value = k;
            value.Value = v;
            command.ExecuteNonQuery();
        }

        Put("format", IndexSchema.FormatVersion.ToString(CultureInfo.InvariantCulture));
        Put("written", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Put("target", snapshot.Target.Path);
        Put("loader", snapshot.Load.Loader);
        Put("overview", JsonSerializer.Serialize(IndexedOverview.From(overview)));
    }

    // ---- Declarations ----

    private static async Task<SymbolTable> DeclarationsAsync(WorkspaceSnapshot snapshot, FileTable files, CancellationToken cancellationToken)
    {
        var found = new ConcurrentBag<(ISymbol Symbol, ProjectEntry? Project)>();
        await Parallel.ForEachAsync(snapshot.Solution.Projects, Options(cancellationToken), async (project, token) =>
        {
            if (await project.GetCompilationAsync(token) is not { } compilation)
            {
                return;
            }

            var entry = snapshot.Projects.Get(project.Id);
            foreach (var type in compilation.GetSymbolsWithName(static _ => true, SymbolFilter.Type, token).OfType<INamedTypeSymbol>())
            {
                if (!type.IsImplicitlyDeclared)
                {
                    found.Add((type, entry));
                }

                foreach (var member in type.GetMembers().Where(IsDeclaration))
                {
                    found.Add((member, entry));
                }
            }
        });

        var table = new SymbolTable();
        foreach (var group in found.GroupBy(f => SymbolFormatter.Id(f.Symbol), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The same variant first on every write: the one of the first project and target framework.
            var variants = group.OrderBy(v => v.Project?.Name, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Project?.TargetFramework, StringComparer.OrdinalIgnoreCase).ToList();
            var row = table.AddSource(group.Key, variants[0].Symbol, cancellationToken);
            foreach (var (symbol, project) in variants)
            {
                if (project is not null)
                {
                    row.Projects.Add(project.Name);
                    if (project.TargetFramework is { } framework)
                    {
                        row.Frameworks.Add(framework);
                    }
                }

                foreach (var location in symbol.Locations.Where(l => l.IsInSource && l.SourceTree?.FilePath is { Length: > 0 }))
                {
                    var span = location.GetLineSpan();
                    row.Declarations.Add((files.Add(location.SourceTree!.FilePath, IndexSchema.SourceFile), span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, location.SourceSpan.Length));
                }
            }
        }

        return table;
    }

    // What dotnet_find_symbols and dotnet_outline list: members as written, not accessors, nested types (listed as types) or compiler-made ones.
    private static bool IsDeclaration(ISymbol member) =>
        !member.IsImplicitlyDeclared
        && member is not INamedTypeSymbol
        && member is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise };

    // ---- References ----

    private sealed record FileReferences(long File, List<ReferenceRow> Rows, Dictionary<int, string> Lines);

    private sealed record ReferenceRow(long Symbol, int Line, int Column, int Length, string Kind, string Project, SortedSet<string> Frameworks, long? Container);

    private static async Task ReferencesAsync(
        WorkspaceSnapshot snapshot, SymbolTable symbols, FileTable files, LabelTable labels, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        // One file at a time across its target-framework variants, in parallel; one writer, so memory holds a few files.
        var channel = Channel.CreateBounded<FileReferences>(new BoundedChannelOptions(64) { SingleReader = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var groups = snapshot.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath is not null)
            .GroupBy(d => d.FilePath!, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();
        var produce = Task.Run(
            async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(groups, Options(stop.Token), async (group, token) =>
                    {
                        var result = await FileAsync(snapshot, symbols, files.Add(group.Key, IndexSchema.SourceFile), [.. group], token);
                        if (result.Rows.Count > 0)
                        {
                            await channel.Writer.WriteAsync(result, token);
                        }
                    });
                    channel.Writer.Complete();
                }
#pragma warning disable CA1031 // Handed to the reader, which rethrows it.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    channel.Writer.Complete(ex);
                }
            },
            CancellationToken.None);
        try
        {
            await WriteReferencesAsync(channel.Reader, labels, connection, transaction, cancellationToken);
            await produce;
        }
        finally
        {
            // A write that failed must not leave the producers waiting on a full channel.
            await stop.CancelAsync();
            await produce.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task WriteReferencesAsync(ChannelReader<FileReferences> reader, LabelTable labels, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO refs(symbol, file, line, col, length, kind, project, frameworks, container) VALUES ($s, $f, $l, $c, $n, $k, $p, $w, $o)";
        var p = ReferenceParameters.Select(n => insert.Parameters.Add(n, n == "$k" ? SqliteType.Text : SqliteType.Integer)).ToArray();
        using var line = connection.CreateCommand();
        line.Transaction = transaction;
        line.CommandText = "INSERT OR IGNORE INTO lines(file, line, text) VALUES ($f, $l, $t)";
        var lf = line.Parameters.Add("$f", SqliteType.Integer);
        var ll = line.Parameters.Add("$l", SqliteType.Integer);
        var lt = line.Parameters.Add("$t", SqliteType.Text);
        await foreach (var file in reader.ReadAllAsync(cancellationToken))
        {
            foreach (var row in file.Rows)
            {
                p[0].Value = row.Symbol;
                p[1].Value = file.File;
                p[2].Value = row.Line;
                p[3].Value = row.Column;
                p[4].Value = row.Length;
                p[5].Value = row.Kind;
                p[6].Value = labels.Id(row.Project);
                p[7].Value = labels.Id(IndexSchema.Join(row.Frameworks));
                p[8].Value = row.Container is { } container ? container : DBNull.Value;
                insert.ExecuteNonQuery();
            }

            foreach (var (number, text) in file.Lines)
            {
                lf.Value = file.File;
                ll.Value = number;
                lt.Value = text;
                line.ExecuteNonQuery();
            }
        }
    }

    private static async Task<FileReferences> FileAsync(WorkspaceSnapshot snapshot, SymbolTable symbols, long file, IReadOnlyList<Document> variants, CancellationToken cancellationToken)
    {
        var rows = new Dictionary<(long Symbol, int Line, int Column), ReferenceRow>();
        var lines = new Dictionary<int, string>();
        foreach (var document in variants)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (root is null || model is null)
            {
                continue;
            }

            var text = await document.GetTextAsync(cancellationToken);
            var project = snapshot.Projects.Get(document.Project.Id);
            var projectName = project?.Name ?? document.Project.Name;
            foreach (var node in root.DescendantNodes())
            {
                if (node is CSharpSyntax.ConstructorDeclarationSyntax or VisualBasicSyntax.ConstructorBlockSyntax)
                {
                    ConstructorCalls(node, model, symbols, rows, projectName, project?.TargetFramework, cancellationToken);
                    continue;
                }

                var implicitNew = node as CSharpSyntax.ImplicitObjectCreationExpressionSyntax;
                if (implicitNew is null && (node is not (CSharpSyntax.SimpleNameSyntax or VisualBasicSyntax.SimpleNameSyntax) || node is CSharpSyntax.IdentifierNameSyntax { IsVar: true }))
                {
                    continue;
                }

                var info = model.GetSymbolInfo(node, cancellationToken);
                if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is not { } bound)
                {
                    continue;
                }

                // A name is found at its identifier (a generic name without its type arguments), new() at its keyword,
                // as Roslyn's reference search places them.
                var token = implicitNew?.NewKeyword ?? node switch
                {
                    CSharpSyntax.SimpleNameSyntax name => name.Identifier,
                    VisualBasicSyntax.SimpleNameSyntax name => name.Identifier,
                    _ => default,
                };
                IEnumerable<ISymbol> targets = implicitNew is null
                    ? Targets(bound, node, model, cancellationToken)
                    : bound is IMethodSymbol { MethodKind: MethodKind.Constructor } ? [bound] : [];
                foreach (var target in targets)
                {
                    var definition = Definition(target);
                    var kind = implicitNew is not null ? "new" : ReferenceFinder.KindFromSyntax(definition, node) ?? ReferenceFinder.Classify(node, model, cancellationToken);
                    var key = symbols.FindSource(SymbolFormatter.Id(definition));
                    if (key is null)
                    {
                        // Methods of referenced assemblies (and implicit constructors) only as callees: their other uses are
                        // everywhere and change nothing.
                        if (kind is not ("call" or "new") || definition is not IMethodSymbol || (definition.Locations.Any(l => l.IsInSource) && !definition.IsImplicitlyDeclared))
                        {
                            continue;
                        }

                        key = symbols.AddExternal(definition);
                    }

                    var span = token.GetLocation().GetLineSpan();
                    var line = span.StartLinePosition.Line + 1;
                    var column = span.StartLinePosition.Character + 1;
                    if (!rows.TryGetValue((key.Value, line, column), out var row))
                    {
                        row = new ReferenceRow(key.Value, line, column, token.Span.Length, implicitNew is null ? kind : IndexSchema.TargetTypedNew, projectName, new SortedSet<string>(StringComparer.OrdinalIgnoreCase), Container(model, node.SpanStart, symbols, cancellationToken));
                        rows[(key.Value, line, column)] = row;

                        // Only references are shown with their line; calls into referenced assemblies are callees, shown by place.
                        if (!symbols.IsExternal(key.Value) && !lines.ContainsKey(line))
                        {
                            lines[line] = Snippet(text, span.StartLinePosition.Line);
                        }
                    }

                    if (project?.TargetFramework is { } framework)
                    {
                        row.Frameworks.Add(framework);
                    }
                }
            }
        }

        return new FileReferences(file, [.. rows.Values], lines);
    }

    /// <summary>
    /// The base or sibling constructor a constructor calls without naming it (<c>: base(...)</c>, <c>MyBase.New</c> or the
    /// implicit call to the base's parameterless one): callees and callers as dotnet_call_hierarchy finds them, kind
    /// "implicit", which find_references leaves out (Roslyn files them as implicit references).
    /// </summary>
    private static void ConstructorCalls(
        SyntaxNode declaration, SemanticModel model, SymbolTable symbols, Dictionary<(long Symbol, int Line, int Column), ReferenceRow> rows,
        string projectName, string? framework, CancellationToken cancellationToken)
    {
        var body = model.GetOperation(declaration, cancellationToken);
        var declared = model.GetDeclaredSymbol(declaration, cancellationToken);
        if (body is null || declared is null || symbols.Find(SymbolFormatter.Id(declared)) is not { } container)
        {
            return;
        }

        foreach (var invocation in body.Descendants().OfType<IInvocationOperation>())
        {
            if (invocation.TargetMethod is not { MethodKind: MethodKind.Constructor } constructor
                || (!invocation.IsImplicit && invocation.Syntax is not (CSharpSyntax.ConstructorInitializerSyntax or VisualBasicSyntax.InvocationExpressionSyntax)))
            {
                continue;
            }

            var definition = constructor.OriginalDefinition;
            var key = symbols.Find(SymbolFormatter.Id(definition)) ?? symbols.AddExternal(definition);
            var span = invocation.Syntax.GetLocation().GetLineSpan();
            var line = span.StartLinePosition.Line + 1;
            var column = span.StartLinePosition.Character + 1;
            if (!rows.TryGetValue((key, line, column), out var row))
            {
                row = new ReferenceRow(key, line, column, 0, "implicit", projectName, new SortedSet<string>(StringComparer.OrdinalIgnoreCase), container);
                rows[(key, line, column)] = row;
            }

            if (framework is not null)
            {
                row.Frameworks.Add(framework);
            }
        }
    }

    /// <summary>
    /// What a name refers to, as Roslyn's reference search files it: a constructor is a use of its type too (and an
    /// attribute names its type), and a created type is a use of the constructor it calls.
    /// </summary>
    private static IEnumerable<ISymbol> Targets(ISymbol bound, SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (bound is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } type } constructor)
        {
            yield return type;
            yield return constructor;
            yield break;
        }

        yield return bound;
        if (bound is INamedTypeSymbol && CreationOf(node) is { } creation && model.GetSymbolInfo(creation, cancellationToken).Symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } called)
        {
            yield return called;
        }
    }

    // The object creation whose type the name is (qualified or not), if any.
    private static SyntaxNode? CreationOf(SyntaxNode node)
    {
        var type = node;
        while (type.Parent is CSharpSyntax.QualifiedNameSyntax { Right: var right } && right == type
            || type.Parent is VisualBasicSyntax.QualifiedNameSyntax { Right: var vbRight } && vbRight == type)
        {
            type = type.Parent;
        }

        return type.Parent switch
        {
            CSharpSyntax.ObjectCreationExpressionSyntax { Type: var created } creation when created == type => creation,
            VisualBasicSyntax.ObjectCreationExpressionSyntax { Type: var vbCreated } creation when vbCreated == type => creation,
            _ => null,
        };
    }

    private static ISymbol Definition(ISymbol symbol)
    {
        var definition = symbol is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : symbol;
        return definition.OriginalDefinition;
    }

    // The member a use is in, as dotnet_call_hierarchy names callers: lambdas and local functions count as their member, accessors as their property.
    private static long? Container(SemanticModel model, int position, SymbolTable symbols, CancellationToken cancellationToken)
    {
        for (var symbol = model.GetEnclosingSymbol(position, cancellationToken); symbol is not null and not INamespaceSymbol; symbol = symbol.ContainingSymbol)
        {
            var member = symbol is IMethodSymbol { AssociatedSymbol: { } associated } ? associated : symbol;
            if (symbols.Find(SymbolFormatter.Id(member)) is { } key)
            {
                return key;
            }
        }

        return null;
    }

    private static string Snippet(SourceText text, int line)
    {
        var snippet = text.Lines[line].ToString().Trim();
        return snippet.Length > MaxSnippetLength ? string.Concat(snippet.AsSpan(0, MaxSnippetLength), "…") : snippet;
    }

    private static void MarkupReferences(MarkupIndex markup, SymbolTable symbols, FileTable files, LabelTable labels, SqliteConnection connection, SqliteTransaction transaction)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO refs(symbol, file, line, col, length, kind, project, frameworks, container) VALUES ($s, $f, $l, $c, 0, 'markup', $p, $w, NULL);
            INSERT OR IGNORE INTO lines(file, line, text) VALUES ($f, $l, $t);
            """;
        var s = insert.Parameters.Add("$s", SqliteType.Integer);
        var f = insert.Parameters.Add("$f", SqliteType.Integer);
        var l = insert.Parameters.Add("$l", SqliteType.Integer);
        var c = insert.Parameters.Add("$c", SqliteType.Integer);
        var p = insert.Parameters.Add("$p", SqliteType.Integer);
        var w = insert.Parameters.Add("$w", SqliteType.Integer);
        var t = insert.Parameters.Add("$t", SqliteType.Text);
        foreach (var (id, reference) in markup.All())
        {
            if (symbols.FindSource(id) is not { } key)
            {
                continue;
            }

            s.Value = key;
            f.Value = files.Add(reference.FilePath, IndexSchema.MarkupFile);
            l.Value = reference.Line;
            c.Value = reference.Column;
            p.Value = labels.Id(reference.Project);
            w.Value = labels.Id(string.Empty);
            t.Value = reference.Snippet;
            insert.ExecuteNonQuery();
        }
    }

    // ---- Hierarchy ----

    /// <summary>
    /// Upwards only, as dotnet_hierarchy lists it: base types and every interface of each type, the overridden members and
    /// implemented interface members of each member. Downwards is the same rows read the other way.
    /// </summary>
    private static void Relations(SymbolTable symbols, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO relations(symbol, relation, target, ordinal, signature) VALUES ($s, $r, $t, $o, $g)";
        var s = insert.Parameters.Add("$s", SqliteType.Integer);
        var r = insert.Parameters.Add("$r", SqliteType.Text);
        var t = insert.Parameters.Add("$t", SqliteType.Integer);
        var o = insert.Parameters.Add("$o", SqliteType.Integer);
        var g = insert.Parameters.Add("$g", SqliteType.Text);
        foreach (var row in symbols.Sources.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordinal = 0;
            foreach (var (relation, related) in Upwards(row.Symbol))
            {
                s.Value = row.Key;
                r.Value = relation;
                t.Value = symbols.Find(SymbolFormatter.Id(related.OriginalDefinition)) ?? symbols.AddExternal(related.OriginalDefinition);
                o.Value = ordinal++;
                g.Value = related.ToDisplayString(SymbolFormatter.Signature);
                insert.ExecuteNonQuery();
            }
        }
    }

    private static IEnumerable<(string Relation, ISymbol Related)> Upwards(ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol type)
        {
            for (var baseType = type.BaseType; baseType is not null && baseType.SpecialType != SpecialType.System_Object; baseType = baseType.BaseType)
            {
                yield return ("base", baseType);
            }

            foreach (var implemented in type.AllInterfaces)
            {
                yield return ("implements", implemented);
            }

            yield break;
        }

        for (var overridden = Overridden(symbol); overridden is not null; overridden = Overridden(overridden))
        {
            yield return ("overrides", overridden);
        }

        if (symbol.ContainingType is { } containing)
        {
            foreach (var interfaceMember in containing.AllInterfaces.SelectMany(i => i.GetMembers()))
            {
                if (SymbolEqualityComparer.Default.Equals(containing.FindImplementationForInterfaceMember(interfaceMember), symbol))
                {
                    yield return ("implements", interfaceMember);
                }
            }
        }
    }

    private static ISymbol? Overridden(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null,
    };

    // ---- Tables ----

    private static void WriteSymbols(SymbolTable symbols, FileTable files, SqliteConnection connection, SqliteTransaction transaction)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO symbols(id, docid, name, kind, match, containers, signature, member, namespace, projects, frameworks, summary, inherits,
                via, parent, depth, language, file, line, start_line, end_line, external, assembly, abstract, type_kind)
            VALUES ($id, $docid, $name, $kind, $match, $containers, $signature, $member, $namespace, $projects, $frameworks, $summary, $inherits,
                $via, $parent, $depth, $language, $file, $line, $start, $end, $external, $assembly, $abstract, $typekind)
            """;
        var names = new[]
        {
            "$id", "$docid", "$name", "$kind", "$match", "$containers", "$signature", "$member", "$namespace", "$projects", "$frameworks", "$summary",
            "$inherits", "$via", "$parent", "$depth", "$language", "$file", "$line", "$start", "$end", "$external", "$assembly", "$abstract", "$typekind",
        };
        var p = names.ToDictionary(n => n, n => insert.Parameters.Add(n, SqliteType.Text));
        using var declaration = connection.CreateCommand();
        declaration.Transaction = transaction;
        declaration.CommandText = "INSERT INTO declarations(symbol, file, line, col, length) VALUES ($s, $f, $l, $c, $n)";
        var ds = declaration.Parameters.Add("$s", SqliteType.Integer);
        var df = declaration.Parameters.Add("$f", SqliteType.Integer);
        var dl = declaration.Parameters.Add("$l", SqliteType.Integer);
        var dc = declaration.Parameters.Add("$c", SqliteType.Integer);
        var dn = declaration.Parameters.Add("$n", SqliteType.Integer);
        foreach (var row in symbols.All)
        {
            var symbol = row.Symbol;
            var source = SourceSpan(symbol, files);
            p["$id"].Value = row.Key;
            p["$docid"].Value = row.Id;
            p["$name"].Value = symbol.Name;
            p["$kind"].Value = SymbolFormatter.Kind(symbol);
            p["$match"].Value = Match(symbol);
            p["$containers"].Value = IndexSchema.Join(Containers(symbol));
            p["$signature"].Value = symbol.ToDisplayString(SymbolFormatter.Signature);
            p["$member"].Value = symbol.ToDisplayString(SymbolFormatter.Member);
            p["$namespace"].Value = SymbolFormatter.Namespace(symbol);
            p["$projects"].Value = IndexSchema.Join(row.Projects);
            p["$frameworks"].Value = IndexSchema.Join(row.Frameworks);
            p["$summary"].Value = (object?)row.Summary ?? DBNull.Value;
            p["$inherits"].Value = IndexSchema.Join(Inherits(symbol));
            p["$via"].Value = symbol.ContainingType is { } containing ? containing.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) : DBNull.Value;
            p["$parent"].Value = symbol.ContainingType is { } parent && symbols.Find(SymbolFormatter.Id(parent)) is { } parentKey ? parentKey : DBNull.Value;
            p["$depth"].Value = Depth(symbol);
            p["$language"].Value = symbol.Language;
            p["$file"].Value = source is { } s ? s.File : DBNull.Value;
            p["$line"].Value = source?.Line ?? 0;
            p["$start"].Value = source?.Start ?? 0;
            p["$end"].Value = source?.End ?? 0;
            p["$external"].Value = row.External ? 1 : 0;
            p["$assembly"].Value = (object?)symbol.ContainingAssembly?.Name ?? DBNull.Value;
            p["$abstract"].Value = symbol.IsAbstract ? 1 : 0;
            p["$typekind"].Value = symbol is INamedTypeSymbol type ? type.TypeKind.ToString() : symbol.ContainingType?.TypeKind.ToString() ?? string.Empty;
            insert.ExecuteNonQuery();

            foreach (var (file, line, column, length) in row.Declarations.Distinct())
            {
                ds.Value = row.Key;
                df.Value = file;
                dl.Value = line;
                dc.Value = column;
                dn.Value = length;
                declaration.ExecuteNonQuery();
            }
        }
    }

    /// <summary>The first source declaration: the line of its name, and the lines of the whole declaration (VB: its block).</summary>
    private static (long File, int Line, int Start, int End)? SourceSpan(ISymbol symbol, FileTable files)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource && l.SourceTree?.FilePath is { Length: > 0 });
        if (location is null)
        {
            return null;
        }

        var line = location.GetLineSpan().StartLinePosition.Line + 1;
        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault(r => r.SyntaxTree == location.SourceTree);
        if (reference is null)
        {
            return (files.Add(location.SourceTree!.FilePath, IndexSchema.SourceFile), line, line, line);
        }

        var node = reference.GetSyntax();
        if (node.Parent is { } parent && parent.SpanStart == node.SpanStart && parent is not ICompilationUnitSyntax)
        {
            node = parent;
        }

        var lines = node.SyntaxTree.GetLineSpan(node.Span);
        return (files.Add(location.SourceTree!.FilePath, IndexSchema.SourceFile), line, lines.StartLinePosition.Line + 1, lines.EndLinePosition.Line + 1);
    }

    /// <summary>The kinds a dotnet_find_symbols kind filter matches, space-separated.</summary>
    internal static string Match(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => string.Join(' ', new[]
        {
            "type",
            type.IsRecord ? "record" : null,
            type.TypeKind switch
            {
                TypeKind.Class when !type.IsRecord => "class",
                TypeKind.Interface => "interface",
                TypeKind.Struct => "struct",
                TypeKind.Enum => "enum",
                TypeKind.Delegate => "delegate",
                _ => null,
            },
        }.OfType<string>()),
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => string.Empty,
    };

    private static List<string> Containers(ISymbol symbol)
    {
        var containers = new List<string>();
        for (var container = symbol.ContainingSymbol; container is not null and not INamespaceSymbol { IsGlobalNamespace: true }; container = container.ContainingSymbol)
        {
            containers.Insert(0, container.Name);
        }

        return containers;
    }

    private static IEnumerable<string> Inherits(ISymbol symbol) =>
        symbol is INamedTypeSymbol type
            ? new[] { type.BaseType }.OfType<INamedTypeSymbol>().Where(b => b.SpecialType != SpecialType.System_Object).Concat(type.Interfaces).Select(b => b.ToDisplayString(SymbolFormatter.Signature))
            : [];

    private static int Depth(ISymbol symbol)
    {
        var depth = 0;
        for (var container = symbol.ContainingType; container is not null; container = container.ContainingType)
        {
            depth++;
        }

        return depth;
    }

    private static ParallelOptions Options(CancellationToken cancellationToken) =>
        new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancellationToken };

    private sealed class SymbolRow(long key, string id, ISymbol symbol, bool external)
    {
        public long Key { get; } = key;

        public string Id { get; } = id;

        public ISymbol Symbol { get; } = symbol;

        public bool External { get; } = external;

        public string? Summary { get; init; }

        public SortedSet<string> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);

        public SortedSet<string> Frameworks { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(long File, int Line, int Column, int Length)> Declarations { get; } = [];
    }

    /// <summary>Declarations by ID; members of referenced assemblies join as they are met (as callees, bases, implemented members).</summary>
    private sealed class SymbolTable
    {
        private readonly ConcurrentDictionary<string, SymbolRow> _rows = new(StringComparer.Ordinal);
        private readonly List<SymbolRow> _sources = [];
        private readonly ConcurrentDictionary<long, byte> _externals = new();
        private long _next;

        public IEnumerable<SymbolRow> Sources => _sources;

        public IEnumerable<SymbolRow> All => _rows.Values.OrderBy(r => r.Key);

        public SymbolRow AddSource(string id, ISymbol symbol, CancellationToken cancellationToken)
        {
            var row = new SymbolRow(++_next, id, symbol, external: false) { Summary = SymbolFormatter.Summary(symbol, cancellationToken) };
            _rows[id] = row;
            _sources.Add(row);
            return row;
        }

        public long? Find(string id) => _rows.TryGetValue(id, out var row) ? row.Key : null;

        /// <summary>A declaration of the workspace, never a member of a referenced assembly met earlier (so what is stored does not depend on the order files are read).</summary>
        public long? FindSource(string id) => _rows.TryGetValue(id, out var row) && !row.External ? row.Key : null;

        public bool IsExternal(long key) => _externals.ContainsKey(key);

        public long AddExternal(ISymbol symbol)
        {
            var row = _rows.GetOrAdd(SymbolFormatter.Id(symbol), id => new SymbolRow(Interlocked.Increment(ref _next), id, symbol, external: true));
            if (row.External)
            {
                _externals.TryAdd(row.Key, 0);
            }

            return row.Key;
        }
    }

    /// <summary>File ids, with each file's time and size read the first time it is met.</summary>
    private sealed class FileTable
    {
        private readonly ConcurrentDictionary<string, (long Id, int Kind, long Ticks, long Size)> _files = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private long _next;

        public long Add(string path, int kind) =>
            _files.GetOrAdd(Path.GetFullPath(path), full =>
            {
                var info = new FileInfo(full);
                return info.Exists ? (Interlocked.Increment(ref _next), kind, info.LastWriteTimeUtc.Ticks, info.Length) : (Interlocked.Increment(ref _next), kind, 0, -1);
            }).Id;

        public void Write(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO files(id, path, kind, ticks, size) VALUES ($id, $path, $kind, $ticks, $size)";
            var id = insert.Parameters.Add("$id", SqliteType.Integer);
            var path = insert.Parameters.Add("$path", SqliteType.Text);
            var kind = insert.Parameters.Add("$kind", SqliteType.Integer);
            var ticks = insert.Parameters.Add("$ticks", SqliteType.Integer);
            var size = insert.Parameters.Add("$size", SqliteType.Integer);
            foreach (var (full, file) in _files)
            {
                // Saved while the index was written: its code may be older than this time, so it is stored as changed.
                var now = new FileInfo(full);
                var same = now.Exists ? now.LastWriteTimeUtc.Ticks == file.Ticks && now.Length == file.Size : file.Size == -1;
                id.Value = file.Id;
                path.Value = full;
                kind.Value = file.Kind;
                ticks.Value = same ? file.Ticks : 0;
                size.Value = same ? file.Size : -2;
                insert.ExecuteNonQuery();
            }
        }
    }

    /// <summary>Project names and framework lists repeat on every reference: stored once each.</summary>
    private sealed class LabelTable
    {
        private readonly Dictionary<string, long> _ids = new(StringComparer.Ordinal);

        public long Id(string text)
        {
            if (!_ids.TryGetValue(text, out var id))
            {
                id = _ids.Count + 1;
                _ids[text] = id;
            }

            return id;
        }

        public void Write(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO labels(id, text) VALUES ($id, $text)";
            var id = insert.Parameters.Add("$id", SqliteType.Integer);
            var text = insert.Parameters.Add("$text", SqliteType.Text);
            foreach (var (label, key) in _ids)
            {
                id.Value = key;
                text.Value = label;
                insert.ExecuteNonQuery();
            }
        }
    }
}

/// <summary>dotnet_overview's facts as the index keeps them (the target comes from the session).</summary>
internal sealed record IndexedOverview(
    IReadOnlyList<ProjectOverview> Projects,
    bool CentralPackageManagement,
    string? GlobalJsonSdk,
    IReadOnlyList<string> BindingRedirectFiles,
    string? Loader)
{
    public static IndexedOverview From(SolutionOverview overview) =>
        new(overview.Projects, overview.CentralPackageManagement, overview.GlobalJsonSdk, overview.BindingRedirectFiles, overview.Loader);

    public SolutionOverview For(WorkspaceTarget target) =>
        new(target, Projects, CentralPackageManagement, GlobalJsonSdk, BindingRedirectFiles) { Loader = Loader };
}

using System.Text;
using Farol.Core;
using Farol.Core.Paths;
using Farol.Engine.CodeActions;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using CS = Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpSyntaxFactory = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using VB = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using VisualBasicSyntaxFactory = Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory;

namespace Farol.Engine.Refactoring;

/// <summary>
/// One parameter of the new signature: an existing parameter by name, or a new one with its type (as the declaring
/// language writes it) and the value every call site passes for it.
/// </summary>
public sealed record SignatureParameter(string Name, string? Type = null, string? Value = null);

/// <summary>
/// Change signature on public APIs (Roslyn offers it only through a dialog): the parameter lists of the method, its
/// overrides and implementations, and the argument lists of every call site in C# and VB, named and omitted
/// arguments included. Parameters can be reordered, removed and added; their types do not change.
/// </summary>
public static class ChangeSignatureRefactoring
{
    public static async Task<RefactoringResult> ComputeAsync(WorkspaceSnapshot snapshot, ISymbol symbol, IReadOnlyList<SignatureParameter> parameters, string root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(parameters);

        var solution = snapshot.Solution;
        var method = Target(symbol);
        var display = method.ToDisplayString(SymbolFormatter.Signature);
        var comparison = method.Language == LanguageNames.VisualBasic ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var slots = await PlanAsync(solution, method, display, parameters, comparison, cancellationToken);
        var family = await FamilyAsync(solution, method, cancellationToken);
        CheckFamily(family, method, display);
        Collisions(family, slots, display, root);

        var notes = new List<string>();
        foreach (var removed in method.Parameters.Where(p => !slots.Any(s => SymbolEqualityComparer.Default.Equals(s.Existing, p))))
        {
            notes.Add($"removes parameter {removed.Name} and its arguments: a body that still uses it no longer compiles, and the check after applying shows where");
        }

        var work = new Dictionary<DocumentId, List<Rewrite>>();
        foreach (var member in family)
        {
            foreach (var reference in Declarations(member))
            {
                var document = solution.GetDocument(reference.SyntaxTree);
                if (document is null || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
                {
                    continue;
                }

                Add(work, document.Id, DeclarationRewrite(await reference.GetSyntaxAsync(cancellationToken), member, slots, model));
            }
        }

        await CallSitesAsync(solution, method, family, slots, work, notes, root, cancellationToken);

        var changed = solution;
        foreach (var (id, rewrites) in work)
        {
            var document = changed.GetDocument(id)!;
            var text = await document.GetTextAsync(cancellationToken);
            changed = changed.WithDocumentText(id, text.WithChanges(Resolve(text, rewrites)));
        }

        var changes = await CodeActionEditor.ComputeAsync(snapshot, changed, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        return new RefactoringResult($"change signature of {display} to ({string.Join(", ", slots.Select(s => s.Name))})", changes, [], notes);
    }

    private static IMethodSymbol Target(ISymbol symbol)
    {
        if (symbol is not IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor } method)
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"Farol changes the parameters of methods and constructors; {SymbolFormatter.Display(symbol)} is neither.",
                "Pass a method, e.g. OrderCalculator.GetTotal.");
        }

        method = method.ReducedFrom ?? method;
        method = method.OriginalDefinition;
        if (!method.Locations.Any(l => l.IsInSource))
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"{SymbolFormatter.Display(method)} is declared in {method.ContainingAssembly?.Name ?? "a referenced assembly"}, not in the workspace's source.",
                "Change a method declared in the solution.");
        }

        var display = SymbolFormatter.Display(method);
        if (method.MethodKind == MethodKind.Constructor && method.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is CS.TypeDeclarationSyntax))
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"{display} is a primary constructor (its parameters follow the type's name), which Farol does not change.",
                "Change its parameters by hand, or turn it into an ordinary constructor first.");
        }

        if (method.OverriddenMethod is { } overridden)
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"{display} overrides {SymbolFormatter.Display(overridden.OriginalDefinition)}, whose signature it must keep.",
                "Change the signature of the overridden method: its overrides change with it.");
        }

        if (Implemented(method) is { } implemented)
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"{display} implements {SymbolFormatter.Display(implemented.OriginalDefinition)}, whose signature it must keep.",
                "Change the signature of the interface member: its implementations change with it.");
        }

        return method;
    }

    private static IMethodSymbol? Implemented(IMethodSymbol method) =>
        method.ExplicitInterfaceImplementations.FirstOrDefault()
        ?? method.ContainingType.AllInterfaces
            .SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
            .FirstOrDefault(m => SymbolEqualityComparer.Default.Equals(method.ContainingType.FindImplementationForInterfaceMember(m)?.OriginalDefinition, method));

    /// <summary>One parameter of the new list: an existing one (by its ordinal in the method) or a new one with its type and call-site value.</summary>
    private sealed record Slot(string Name, IParameterSymbol? Existing, ITypeSymbol? Type, string? Value);

    private static async Task<List<Slot>> PlanAsync(Solution solution, IMethodSymbol method, string display, IReadOnlyList<SignatureParameter> parameters, StringComparison comparison, CancellationToken cancellationToken)
    {
        var declaration = method.DeclaringSyntaxReferences[0];
        var model = await solution.GetDocument(declaration.SyntaxTree)!.GetSemanticModelAsync(cancellationToken);
        var slots = new List<Slot>();
        foreach (var parameter in parameters)
        {
            var name = (parameter.Name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                throw new FarolException(ErrorCodes.InvalidArgument, "A parameter has no name.", "Each parameter needs a name: an existing parameter's, or a new one with type and value.");
            }

            if (slots.Any(s => string.Equals(s.Name, name, comparison)))
            {
                throw new FarolException(ErrorCodes.InvalidArgument, $"Parameter '{name}' is listed twice.", "List each parameter once, in the new order.");
            }

            var existing = method.Parameters.FirstOrDefault(p => string.Equals(p.Name, name, comparison));
            if (existing is not null)
            {
                if (!string.IsNullOrWhiteSpace(parameter.Type) || !string.IsNullOrWhiteSpace(parameter.Value))
                {
                    throw new FarolException(
                        ErrorCodes.InvalidArgument,
                        $"'{name}' is an existing parameter of {display}: Farol keeps its type and its arguments.",
                        "Pass only its name; give a type and a value only to new parameters.");
                }

                slots.Add(new Slot(existing.Name, existing, null, null));
                continue;
            }

            if (string.IsNullOrWhiteSpace(parameter.Type) || string.IsNullOrWhiteSpace(parameter.Value))
            {
                throw new FarolException(
                    ErrorCodes.InvalidArgument,
                    $"'{name}' is not a parameter of {display} ({string.Join(", ", method.Parameters.Select(p => p.Name))}), so it is a new one and needs a type and a value for the call sites.",
                    "Pass the existing names as they are, and for a new parameter its type (e.g. decimal) and the value callers pass (e.g. 0).");
            }

            RenameRefactoring.ValidateName(solution, method, name);
            slots.Add(new Slot(name, null, ResolveType(model!, declaration.Span.Start, parameter.Type.Trim(), display), parameter.Value.Trim()));
        }

        if (slots.Count == method.Parameters.Length && slots.Select((s, i) => s.Existing?.Ordinal == i).All(same => same))
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"{display} already has these parameters, in this order.", "Pass the new ordered list: existing names, and new ones with a type and a value.");
        }

        if (method.IsExtensionMethod && slots.FirstOrDefault()?.Existing?.Ordinal != 0)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"{display} is an extension method: its first parameter ({method.Parameters[0].Name}) stays first.", $"Keep {method.Parameters[0].Name} first.");
        }

        if (slots.FindIndex(s => s.Existing is { IsParams: true }) is var paramsAt and >= 0 && paramsAt != slots.Count - 1)
        {
            throw new FarolException(ErrorCodes.InvalidArgument, $"{slots[paramsAt].Name} is a params array, which must stay the last parameter.", $"List {slots[paramsAt].Name} last.");
        }

        var firstOptional = slots.FindIndex(s => s.Existing is { IsOptional: true });
        if (firstOptional >= 0 && slots.Skip(firstOptional).FirstOrDefault(s => s.Existing is not { IsOptional: true } && s.Existing is not { IsParams: true }) is { } required)
        {
            throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"{required.Name} would follow the optional parameter {slots[firstOptional].Name}, and a required parameter cannot follow an optional one.",
                $"List {required.Name} before the optional parameters.");
        }

        return slots;
    }

    private static ITypeSymbol ResolveType(SemanticModel model, int position, string text, string display)
    {
        ITypeSymbol? type = null;
        if (model.Language == LanguageNames.VisualBasic)
        {
            var syntax = VisualBasicSyntaxFactory.ParseTypeName(text);
            if (!syntax.ContainsDiagnostics && syntax.Span.Length == text.Length)
            {
                type = Microsoft.CodeAnalysis.VisualBasic.VisualBasicExtensions.GetSpeculativeTypeInfo(model, position, syntax, SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
            }
        }
        else
        {
            var syntax = CSharpSyntaxFactory.ParseTypeName(text);
            if (!syntax.ContainsDiagnostics && syntax.Span.Length == text.Length)
            {
                type = Microsoft.CodeAnalysis.CSharp.CSharpExtensions.GetSpeculativeTypeInfo(model, position, syntax, SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
            }
        }

        return type is { TypeKind: not TypeKind.Error }
            ? type
            : throw new FarolException(
                ErrorCodes.InvalidArgument,
                $"'{text}' is not a type known where {display} is declared ({(model.Language == LanguageNames.VisualBasic ? "Visual Basic" : "C#")}).",
                "Write the type as that file does, e.g. decimal in C# or Decimal in Visual Basic, with its namespace if the file does not import it.");
    }

    // The method, its overrides and implementations, and theirs: every declaration whose parameters change together.
    private static async Task<List<IMethodSymbol>> FamilyAsync(Solution solution, IMethodSymbol method, CancellationToken cancellationToken)
    {
        var family = new List<IMethodSymbol> { method };
        for (var i = 0; i < family.Count; i++)
        {
            var member = family[i];
            var related = (await SymbolFinder.FindOverridesAsync(member, solution, cancellationToken: cancellationToken)).ToList();
            if (member.ContainingType.TypeKind == TypeKind.Interface)
            {
                related.AddRange(await SymbolFinder.FindImplementationsAsync(member, solution, cancellationToken: cancellationToken));
            }

            foreach (var next in related.OfType<IMethodSymbol>().Select(m => m.OriginalDefinition).Where(m => m.Locations.Any(l => l.IsInSource)))
            {
                if (!family.Contains(next, SymbolEqualityComparer.Default))
                {
                    family.Add(next);
                }
            }
        }

        return family;
    }

    /// <summary>
    /// Every declaration of the family must change with the method: one in another language would get the type written
    /// as the method's language writes it, and one that also implements an interface member would no longer implement it.
    /// </summary>
    private static void CheckFamily(List<IMethodSymbol> family, IMethodSymbol method, string display)
    {
        var ids = family.Select(m => SymbolFormatter.Id(m)).ToHashSet(StringComparer.Ordinal);
        foreach (var member in family.Skip(1))
        {
            if (member.Language != method.Language)
            {
                throw new FarolException(
                    ErrorCodes.InvalidArgument,
                    $"{SymbolFormatter.Display(member)} overrides or implements {display} in {LanguageName(member.Language)}, while {display} is in {LanguageName(method.Language)}: Farol changes a family of declarations written in one language.",
                    "Change the signatures by hand, then run dotnet_check.");
            }

            if (Implemented(member) is { } implemented && !ids.Contains(SymbolFormatter.Id(implemented.OriginalDefinition)))
            {
                throw new FarolException(
                    ErrorCodes.InvalidArgument,
                    $"{SymbolFormatter.Display(member)} changes with {display} but also implements {SymbolFormatter.Display(implemented.OriginalDefinition)}, whose signature it must keep.",
                    "Change the interface member's signature too, or split the implementation first.");
            }
        }
    }

    private static string LanguageName(string language) => language == LanguageNames.VisualBasic ? "Visual Basic" : "C#";

    // Another overload with the new parameter types would make the declarations clash.
    private static void Collisions(List<IMethodSymbol> family, List<Slot> slots, string display, string root)
    {
        foreach (var member in family)
        {
            var types = slots.Select(s => s.Existing is { } e ? member.Parameters[e.Ordinal].Type : s.Type!).ToList();
            var clash = member.ContainingType.GetMembers(member.Name).OfType<IMethodSymbol>()
                .Where(o => !SymbolEqualityComparer.Default.Equals(o.OriginalDefinition, member) && o.Parameters.Length == types.Count)
                .FirstOrDefault(o => o.Parameters.Select(p => p.Type).SequenceEqual(types, SymbolEqualityComparer.Default));
            if (clash is not null)
            {
                throw new FarolException(
                    ErrorCodes.Conflict,
                    $"{SymbolFormatter.Display(clash)} at {SymbolFormatter.Location(clash, root)} already has the new parameter types of {display}. Nothing was written.",
                    "Pick another order or other parameters, or rename one of the overloads first (dotnet_rename).");
            }
        }
    }

    private static IEnumerable<SyntaxReference> Declarations(IMethodSymbol member) =>
        member.DeclaringSyntaxReferences
            .Concat(member.PartialImplementationPart?.DeclaringSyntaxReferences ?? [])
            .Concat(member.PartialDefinitionPart?.DeclaringSyntaxReferences ?? [])
            .Distinct();

    /// <summary>A change to one span, built from the text of that span with the changes nested inside it applied.</summary>
    private sealed record Rewrite(TextSpan Span, Func<Func<TextSpan, string>, string> Build);

    private static void Add(Dictionary<DocumentId, List<Rewrite>> work, DocumentId id, Rewrite? rewrite)
    {
        if (rewrite is null)
        {
            return;
        }

        if (!work.TryGetValue(id, out var list))
        {
            work[id] = list = [];
        }

        if (!list.Any(r => r.Span == rewrite.Span))
        {
            list.Add(rewrite);
        }
    }

    // Inner spans first, so a call inside another call's arguments is rewritten before the outer list is rebuilt from it.
    private static List<TextChange> Resolve(SourceText text, List<Rewrite> rewrites)
    {
        var done = new List<TextChange>();
        foreach (var rewrite in rewrites.OrderBy(r => r.Span.Length).ThenBy(r => r.Span.Start))
        {
            string TextOf(TextSpan span)
            {
                var inner = done.Where(c => span.Contains(c.Span)).OrderByDescending(c => c.Span.Start);
                var builder = new StringBuilder(text.ToString(span));
                foreach (var change in inner)
                {
                    builder.Remove(change.Span.Start - span.Start, change.Span.Length).Insert(change.Span.Start - span.Start, change.NewText);
                }

                return builder.ToString();
            }

            var newText = rewrite.Build(TextOf);
            done.RemoveAll(c => rewrite.Span.Contains(c.Span));
            done.Add(new TextChange(rewrite.Span, newText));
        }

        return done;
    }

    private static Rewrite? DeclarationRewrite(SyntaxNode node, IMethodSymbol member, List<Slot> slots, SemanticModel model)
    {
        string Item(Slot slot, IReadOnlyList<SyntaxNode> existing, Func<TextSpan, string> textOf) => slot.Existing is { } e
            ? textOf(existing[e.Ordinal].Span)
            : model.Language == LanguageNames.VisualBasic
                ? $"{slot.Name} As {slot.Type!.ToMinimalDisplayString(model, node.SpanStart)}"
                : $"{slot.Type!.ToMinimalDisplayString(model, node.SpanStart)} {slot.Name}";

        switch (node)
        {
            case CS.BaseMethodDeclarationSyntax { ParameterList: var list }:
                return new Rewrite(TextSpan.FromBounds(list.OpenParenToken.Span.End, list.CloseParenToken.SpanStart), textOf => string.Join(", ", slots.Select(s => Item(s, list.Parameters, textOf))));
            case VB.MethodBaseSyntax { ParameterList: { } vbList }:
                return new Rewrite(TextSpan.FromBounds(vbList.OpenParenToken.Span.End, vbList.CloseParenToken.SpanStart), textOf => string.Join(", ", slots.Select(s => Item(s, vbList.Parameters, textOf))));
            case VB.MethodStatementSyntax { ParameterList: null } statement:
                var after = statement.TypeParameterList?.Span.End ?? statement.Identifier.Span.End;
                return new Rewrite(new TextSpan(after, 0), textOf => $"({string.Join(", ", slots.Select(s => Item(s, [], textOf)))})");
            case VB.SubNewStatementSyntax { ParameterList: null } constructor:
                return new Rewrite(new TextSpan(constructor.NewKeyword.Span.End, 0), textOf => $"({string.Join(", ", slots.Select(s => Item(s, [], textOf)))})");
            default:
                _ = member;
                return null;
        }
    }

    /// <summary>An argument as written: its name when named, the span of what it passes, or an omitted VB argument.</summary>
    private sealed record Argument(string? Name, TextSpan Span, bool Omitted, bool Simple = true);

    /// <summary>A call: its node, its arguments, the span inside its parentheses (or where to add them), and the language.</summary>
    private sealed record Call(SyntaxNode Node, IReadOnlyList<Argument> Arguments, TextSpan? Inside, int InsertAt, bool VisualBasic);

    private static async Task CallSitesAsync(
        Solution solution, IMethodSymbol method, List<IMethodSymbol> family, List<Slot> slots, Dictionary<DocumentId, List<Rewrite>> work, List<string> notes, string root, CancellationToken cancellationToken)
    {
        // By id: a call in another project binds to that project's view of the method, which is another symbol instance.
        var ids = family.Select(m => SymbolFormatter.Id(m)).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<(DocumentId, int)>();
        foreach (var group in await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken))
        {
            if (group.Definition is not IMethodSymbol definition || !ids.Contains(SymbolFormatter.Id(definition.OriginalDefinition)))
            {
                continue;
            }

            foreach (var reference in group.Locations.Where(l => !l.IsCandidateLocation))
            {
                var document = reference.Document;
                var where = DisplayPath.Location(root, document.FilePath ?? document.Name, reference.Location.GetLineSpan().StartLinePosition.Line + 1);
                if (reference.IsImplicit)
                {
                    if (slots.Any(s => s.Existing is null))
                    {
                        notes.Add($"{where} calls {method.Name} implicitly (a constructor without an explicit base call): add the new arguments there by hand");
                    }

                    continue;
                }

                if (!seen.Add((document.Id, reference.Location.SourceSpan.Start))
                    || await document.GetSyntaxRootAsync(cancellationToken) is not { } syntaxRoot
                    || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
                {
                    continue;
                }

                var token = syntaxRoot.FindToken(reference.Location.SourceSpan.Start, findInsideTrivia: true);
                if (token.Parent?.IsPartOfStructuredTrivia() == true)
                {
                    // A documentation reference (cref): it names the method, not a call.
                    continue;
                }

                var call = document.Project.Language == LanguageNames.VisualBasic ? VisualBasicCall(token) : CSharpCall(token);
                var bound = call is null ? null : Bound(model, call.Node, cancellationToken);
                if (call is null || bound is null || !ids.Contains(SymbolFormatter.Id((bound.ReducedFrom ?? bound).OriginalDefinition)))
                {
                    notes.Add($"{where} uses {method.Name} without calling it (a delegate, AddressOf or an attribute): left as is; the check after applying shows whether it still fits");
                    continue;
                }

                if (CallRewrite(call, bound, slots, where, notes) is { } rewrite)
                {
                    Add(work, document.Id, rewrite);
                    if (document.Project.Language != method.Language && slots.Any(s => s.Existing is null))
                    {
                        var note = $"{DisplayPath.From(root, document.FilePath ?? document.Name)} calls {method.Name} from {LanguageName(document.Project.Language)}: the new arguments are written there as given, so check that they read as {LanguageName(document.Project.Language)}";
                        if (!notes.Contains(note))
                        {
                            notes.Add(note);
                        }
                    }
                }
            }
        }
    }

    private static IMethodSymbol? Bound(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        var info = model.GetSymbolInfo(node, cancellationToken);
        return (info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null)) as IMethodSymbol;
    }

    // The call whose callee is the token: climbing through the names of the callee, never into an argument.
    private static Call? CSharpCall(SyntaxToken token)
    {
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case CS.InvocationExpressionSyntax invocation when !invocation.ArgumentList.Span.Contains(token.Span):
                    return CSharpCall(invocation, invocation.ArgumentList);
                case CS.ObjectCreationExpressionSyntax creation when creation.Type.Span.Contains(token.Span):
                    return CSharpCall(creation, creation.ArgumentList, creation.Type.Span.End);
                case CS.ImplicitObjectCreationExpressionSyntax implicitCreation:
                    return CSharpCall(implicitCreation, implicitCreation.ArgumentList);
                case CS.ConstructorInitializerSyntax initializer:
                    return CSharpCall(initializer, initializer.ArgumentList);
                case CS.ArgumentSyntax or CS.ArgumentListSyntax or CS.AttributeSyntax or CS.StatementSyntax or CS.MemberDeclarationSyntax or CS.LambdaExpressionSyntax:
                    return null;
            }
        }

        return null;
    }

    private static Call CSharpCall(SyntaxNode node, CS.BaseArgumentListSyntax? list, int insertAt = 0)
    {
        if (list is null)
        {
            return new Call(node, [], null, insertAt, VisualBasic: false);
        }

        var arguments = list.Arguments
            .Select(a => new Argument(
                a.NameColon?.Name.Identifier.ValueText,
                TextSpan.FromBounds(a.RefKindKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None) ? a.Expression.SpanStart : a.RefKindKeyword.SpanStart, a.Span.End),
                Omitted: false,
                Simple: a.Expression is CS.IdentifierNameSyntax or CS.LiteralExpressionSyntax or CS.ThisExpressionSyntax or CS.MemberAccessExpressionSyntax { Expression: CS.IdentifierNameSyntax or CS.ThisExpressionSyntax }))
            .ToList();
        var open = list is CS.ArgumentListSyntax parens ? parens.OpenParenToken : ((CS.BracketedArgumentListSyntax)list).OpenBracketToken;
        var close = list is CS.ArgumentListSyntax p ? p.CloseParenToken : ((CS.BracketedArgumentListSyntax)list).CloseBracketToken;
        return new Call(node, arguments, TextSpan.FromBounds(open.Span.End, close.SpanStart), insertAt, VisualBasic: false);
    }

    private static Call? VisualBasicCall(SyntaxToken token)
    {
        // A call without parentheses (x = obj.Total, Dim v = Total): the name is the callee, and the list goes after it.
        if (token.Parent is VB.SimpleNameSyntax name)
        {
            VB.ExpressionSyntax callee = name.Parent is VB.MemberAccessExpressionSyntax access && access.Name == name ? access : name;
            if (callee.Parent is not VB.InvocationExpressionSyntax invocation || invocation.Expression != callee)
            {
                return callee.Parent is VB.UnaryExpressionSyntax or VB.NameOfExpressionSyntax or VB.QualifiedNameSyntax
                    || callee.Ancestors().Any(a => a is VB.AttributeSyntax or VB.ImportsStatementSyntax or VB.CrefReferenceSyntax)
                    || Microsoft.CodeAnalysis.VisualBasic.SyntaxFacts.IsInNamespaceOrTypeContext(callee)
                    ? null
                    : new Call(callee, [], null, callee.Span.End, VisualBasic: true);
            }
        }

        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case VB.InvocationExpressionSyntax invocation when invocation.ArgumentList?.Span.Contains(token.Span) != true:
                    return VisualBasicCall(invocation, invocation.ArgumentList, invocation.Expression?.Span.End ?? invocation.Span.End);
                case VB.ObjectCreationExpressionSyntax creation when creation.Type.Span.Contains(token.Span):
                    return VisualBasicCall(creation, creation.ArgumentList, creation.Type.Span.End);
                case VB.ArgumentSyntax or VB.ArgumentListSyntax or VB.AttributeSyntax or VB.StatementSyntax or VB.LambdaExpressionSyntax or VB.UnaryExpressionSyntax:
                    return null;
            }
        }

        return null;
    }

    private static Call VisualBasicCall(SyntaxNode node, VB.ArgumentListSyntax? list, int insertAt)
    {
        if (list is null)
        {
            return new Call(node, [], null, insertAt, VisualBasic: true);
        }

        var arguments = list.Arguments
            .Select(a => a switch
            {
                VB.SimpleArgumentSyntax simple => new Argument(
                    simple.NameColonEquals?.Name.Identifier.ValueText,
                    simple.Expression.Span,
                    Omitted: false,
                    Simple: simple.Expression is VB.IdentifierNameSyntax or VB.LiteralExpressionSyntax or VB.MeExpressionSyntax or VB.MemberAccessExpressionSyntax { Expression: VB.IdentifierNameSyntax or VB.MeExpressionSyntax }),
                _ => new Argument(null, a.Span, Omitted: true),
            })
            .ToList();
        return new Call(node, arguments, TextSpan.FromBounds(list.OpenParenToken.Span.End, list.CloseParenToken.SpanStart), insertAt, VisualBasic: true);
    }

    /// <summary>The call's new argument list: each argument moves with its parameter; after a missing one, the rest are named.</summary>
    private static Rewrite? CallRewrite(Call call, IMethodSymbol bound, List<Slot> slots, string where, List<string> notes)
    {
        var definition = bound.ReducedFrom ?? bound;
        var offset = bound.ReducedFrom is null ? 0 : 1;
        var comparison = call.VisualBasic ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var paramsOrdinal = definition.Parameters.LastOrDefault(p => p.IsParams)?.Ordinal;
        var byOrdinal = new Dictionary<int, List<Argument>>();
        var position = 0;
        foreach (var argument in call.Arguments)
        {
            int ordinal;
            if (argument.Name is { } name)
            {
                if (definition.Parameters.FirstOrDefault(p => string.Equals(p.Name, name, comparison)) is not { } named)
                {
                    throw new FarolException(ErrorCodes.InvalidArgument, $"{where} passes an argument named '{name}' that {definition.Name} does not have.", "Fix that call first: it does not compile.");
                }

                ordinal = named.Ordinal;
            }
            else
            {
                ordinal = position + offset;
                if (paramsOrdinal is { } p && ordinal > p)
                {
                    ordinal = p;
                }

                position++;
            }

            if (!argument.Omitted)
            {
                if (!byOrdinal.TryGetValue(ordinal, out var list))
                {
                    byOrdinal[ordinal] = list = [];
                }

                list.Add(argument);
            }
        }

        // Arguments are evaluated in their new order, and a removed one is not evaluated at all.
        var kept = slots.Where(s => s.Existing is not null).Select(s => s.Existing!.Ordinal).ToList();
        var reordered = !kept.SequenceEqual(kept.Order());
        var effects = byOrdinal.Where(p => !p.Value.TrueForAll(a => a.Simple)).Select(p => p.Key).ToList();
        if (effects.Any(o => !kept.Contains(o)) || (reordered && effects.Count > 0 && byOrdinal.Count > 1))
        {
            notes.Add($"{where} passes an expression that may have side effects (a call, an increment): removing or reordering parameters changes whether or when it runs");
        }

        var parameterNames = definition.Parameters.Select(p => p.Name).ToList();
        string Named(string name, string text) => call.VisualBasic ? $"{name}:={text}" : $"{name}: {text}";

        string Build(Func<TextSpan, string> textOf)
        {
            var parts = new List<string>();
            var named = false;
            foreach (var slot in slots.Skip(offset))
            {
                if (slot.Existing is not { } existing)
                {
                    parts.Add(named ? Named(slot.Name, slot.Value!) : slot.Value!);
                    continue;
                }

                if (!byOrdinal.TryGetValue(existing.Ordinal, out var arguments) || arguments.Count == 0)
                {
                    // Omitted optional argument: what follows can no longer go by position.
                    named = true;
                    continue;
                }

                if (arguments.Count > 1)
                {
                    if (named)
                    {
                        throw new FarolException(
                            ErrorCodes.InvalidArgument,
                            $"{where} passes several values to the params array {existing.Name} after an omitted argument, which cannot be written with named arguments.",
                            "Pass the params values as an array at that call first.");
                    }

                    parts.AddRange(arguments.Select(a => textOf(a.Span)));
                    continue;
                }

                var argument = arguments[0];
                named |= argument.Name is not null;
                parts.Add(named ? Named(parameterNames[existing.Ordinal], textOf(argument.Span)) : textOf(argument.Span));
            }

            return string.Join(", ", parts);
        }

        if (call.Inside is { } inside)
        {
            return new Rewrite(inside, Build);
        }

        var anyArguments = slots.Skip(offset).Any(s => s.Existing is null);
        return anyArguments ? new Rewrite(new TextSpan(call.InsertAt, 0), textOf => $"({Build(textOf)})") : null;
    }
}

using System.Text;
using Microsoft.CodeAnalysis;
using CSharpKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind;
using CSharpSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;
using VisualBasicKind = Microsoft.CodeAnalysis.VisualBasic.SyntaxKind;
using VisualBasicSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Farol.Engine.Diagnostics;

/// <summary>How far a changed declaration can reach.</summary>
internal enum DeclarationKind
{
    /// <summary>A type header: attributes, modifiers, name, type parameters, base types and constraints.</summary>
    Type,

    /// <summary>A member signature, bodies and non-constant initializers left out; its uses name it.</summary>
    Member,

    /// <summary>Operators, conversions, indexers and default properties: used without their name.</summary>
    UnnamedMember,

    /// <summary>A using/Imports or Option directive, a destructor, top-level statements: only its own file is affected.</summary>
    FileOnly,

    /// <summary>A global using: every file of the project binds differently.</summary>
    ProjectWide,

    /// <summary>Assembly or module attributes, extern aliases, or syntax not recognized: anything may change.</summary>
    Unknown,
}

/// <summary>One declaration of a file, as the other files of the solution see it.</summary>
internal sealed record Declaration(DeclarationKind Kind, string Container, string Name, string Signature, SyntaxNode Node)
{
    public string Key => $"{Kind}|{Container}|{Signature}";
}

/// <summary>
/// Reads what a C# or VB file declares from its syntax alone, so two versions of a file can be compared without
/// compiling either: a declaration whose key is in one version and not the other was added, removed or changed.
/// </summary>
internal static class DeclarationShape
{
    public static IReadOnlyList<Declaration> Of(SyntaxNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var declarations = new List<Declaration>();
        switch (root)
        {
            case CSharpSyntax.CompilationUnitSyntax unit:
                CSharpHeader(unit.Externs, unit.Usings, string.Empty, declarations);
                foreach (var attributes in unit.AttributeLists)
                {
                    declarations.Add(new(DeclarationKind.Unknown, string.Empty, string.Empty, Tokens(attributes), attributes));
                }

                CSharp(unit.Members, string.Empty, declarations);
                break;
            case VisualBasicSyntax.CompilationUnitSyntax unit:
                foreach (var directive in unit.Options.Cast<SyntaxNode>().Concat(unit.Imports))
                {
                    declarations.Add(new(DeclarationKind.FileOnly, string.Empty, string.Empty, Tokens(directive), directive));
                }

                foreach (var attributes in unit.Attributes)
                {
                    declarations.Add(new(DeclarationKind.Unknown, string.Empty, string.Empty, Tokens(attributes), attributes));
                }

                VisualBasic(unit.Members, string.Empty, declarations);
                break;
            default:
                declarations.Add(new(DeclarationKind.Unknown, string.Empty, string.Empty, Tokens(root), root));
                break;
        }

        return declarations;
    }

    private static void CSharpHeader(
        SyntaxList<CSharpSyntax.ExternAliasDirectiveSyntax> externs, SyntaxList<CSharpSyntax.UsingDirectiveSyntax> usings, string container, List<Declaration> output)
    {
        foreach (var alias in externs)
        {
            output.Add(new(DeclarationKind.Unknown, container, string.Empty, Tokens(alias), alias));
        }

        foreach (var directive in usings)
        {
            var kind = directive.GlobalKeyword.IsKind(CSharpKind.GlobalKeyword) ? DeclarationKind.ProjectWide : DeclarationKind.FileOnly;
            output.Add(new(kind, container, string.Empty, Tokens(directive), directive));
        }
    }

    private static void CSharp(SyntaxList<CSharpSyntax.MemberDeclarationSyntax> members, string container, List<Declaration> output)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case CSharpSyntax.GlobalStatementSyntax statement:
                    output.Add(new(DeclarationKind.FileOnly, container, string.Empty, Tokens(statement), statement));
                    break;
                case CSharpSyntax.BaseNamespaceDeclarationSyntax space:
                    {
                        var name = Join(container, space.Name.ToString());
                        CSharpHeader(space.Externs, space.Usings, name, output);
                        CSharp(space.Members, name, output);
                        break;
                    }

                case CSharpSyntax.EnumDeclarationSyntax enumeration:
                    {
                        var name = enumeration.Identifier.ValueText;
                        output.Add(new(DeclarationKind.Type, container, name, Tokens(enumeration, [.. enumeration.Members]), enumeration));
                        foreach (var value in enumeration.Members)
                        {
                            output.Add(new(DeclarationKind.Member, Join(container, name), value.Identifier.ValueText, Tokens(value), value));
                        }

                        break;
                    }

                case CSharpSyntax.TypeDeclarationSyntax type:
                    {
                        var name = type.Identifier.ValueText + Arity(type.TypeParameterList?.Parameters.Count);
                        output.Add(new(DeclarationKind.Type, container, name, Tokens(type, [.. type.Members]), type));
                        CSharp(type.Members, Join(container, name), output);
                        break;
                    }

                case CSharpSyntax.DelegateDeclarationSyntax @delegate:
                    output.Add(new(DeclarationKind.Type, container, @delegate.Identifier.ValueText + Arity(@delegate.TypeParameterList?.Parameters.Count), Tokens(@delegate), @delegate));
                    break;
                case CSharpSyntax.MethodDeclarationSyntax method:
                    output.Add(new(DeclarationKind.Member, container, method.Identifier.ValueText, Tokens(method, method.Body, method.ExpressionBody), method));
                    break;
                case CSharpSyntax.ConstructorDeclarationSyntax constructor:
                    // A constructor is used through its type's name: new Order(...).
                    output.Add(new(DeclarationKind.Member, container, constructor.Identifier.ValueText, Tokens(constructor, constructor.Body, constructor.ExpressionBody, constructor.Initializer), constructor));
                    break;
                case CSharpSyntax.DestructorDeclarationSyntax destructor:
                    output.Add(new(DeclarationKind.FileOnly, container, string.Empty, Tokens(destructor, destructor.Body, destructor.ExpressionBody), destructor));
                    break;
                case CSharpSyntax.OperatorDeclarationSyntax or CSharpSyntax.ConversionOperatorDeclarationSyntax or CSharpSyntax.IndexerDeclarationSyntax:
                    output.Add(new(DeclarationKind.UnnamedMember, container, string.Empty, Tokens(member, Bodies(member)), member));
                    break;
                case CSharpSyntax.PropertyDeclarationSyntax property:
                    output.Add(new(DeclarationKind.Member, container, property.Identifier.ValueText, Tokens(property, Bodies(property)), property));
                    break;
                case CSharpSyntax.EventDeclarationSyntax @event:
                    output.Add(new(DeclarationKind.Member, container, @event.Identifier.ValueText, Tokens(@event, Bodies(@event)), @event));
                    break;
                case CSharpSyntax.BaseFieldDeclarationSyntax field:
                    {
                        var constant = field.Modifiers.Any(CSharpKind.ConstKeyword);
                        var shared = Tokens(field, [.. field.Declaration.Variables]);
                        foreach (var variable in field.Declaration.Variables)
                        {
                            var value = constant && variable.Initializer is { } initializer ? " " + Tokens(initializer) : string.Empty;
                            output.Add(new(DeclarationKind.Member, container, variable.Identifier.ValueText, $"{shared} {variable.Identifier.ValueText}{value}", variable));
                        }

                        break;
                    }

                default:
                    output.Add(new(DeclarationKind.Unknown, container, string.Empty, Tokens(member), member));
                    break;
            }
        }
    }

    private static void VisualBasic(SyntaxList<VisualBasicSyntax.StatementSyntax> members, string container, List<Declaration> output)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case VisualBasicSyntax.NamespaceBlockSyntax space:
                    VisualBasic(space.Members, Join(container, space.NamespaceStatement.Name.ToString()), output);
                    break;
                case VisualBasicSyntax.EnumBlockSyntax enumeration:
                    {
                        var name = enumeration.EnumStatement.Identifier.ValueText;
                        output.Add(new(DeclarationKind.Type, container, name, Tokens(enumeration.EnumStatement), enumeration.EnumStatement));
                        foreach (var value in enumeration.Members.OfType<VisualBasicSyntax.EnumMemberDeclarationSyntax>())
                        {
                            output.Add(new(DeclarationKind.Member, Join(container, name), value.Identifier.ValueText, Tokens(value), value));
                        }

                        break;
                    }

                case VisualBasicSyntax.TypeBlockSyntax type:
                    {
                        var statement = type.BlockStatement;
                        var name = statement.Identifier.ValueText + Arity(statement.TypeParameterList?.Parameters.Count);
                        var header = string.Join(' ', [Tokens(statement), .. type.Inherits.Select(i => Tokens(i)), .. type.Implements.Select(i => Tokens(i))]);
                        output.Add(new(DeclarationKind.Type, container, name, header, statement));
                        VisualBasic(type.Members, Join(container, name), output);
                        break;
                    }

                case VisualBasicSyntax.DelegateStatementSyntax @delegate:
                    output.Add(new(DeclarationKind.Type, container, @delegate.Identifier.ValueText + Arity(@delegate.TypeParameterList?.Parameters.Count), Tokens(@delegate), @delegate));
                    break;
                case VisualBasicSyntax.MethodBlockSyntax method:
                    output.Add(new(DeclarationKind.Member, container, method.SubOrFunctionStatement.Identifier.ValueText, Tokens(method.SubOrFunctionStatement), method.SubOrFunctionStatement));
                    break;
                case VisualBasicSyntax.ConstructorBlockSyntax constructor:
                    // Used through the type's name: New Order(...).
                    output.Add(new(DeclarationKind.Member, container, TypeName(container), Tokens(constructor.SubNewStatement), constructor.SubNewStatement));
                    break;
                case VisualBasicSyntax.OperatorBlockSyntax @operator:
                    output.Add(new(DeclarationKind.UnnamedMember, container, string.Empty, Tokens(@operator.OperatorStatement), @operator.OperatorStatement));
                    break;
                case VisualBasicSyntax.MethodStatementSyntax method:
                    output.Add(new(DeclarationKind.Member, container, method.Identifier.ValueText, Tokens(method), method));
                    break;
                case VisualBasicSyntax.DeclareStatementSyntax declare:
                    output.Add(new(DeclarationKind.Member, container, declare.Identifier.ValueText, Tokens(declare), declare));
                    break;
                case VisualBasicSyntax.PropertyBlockSyntax property:
                    output.Add(Property(property.PropertyStatement, container, string.Join(' ', property.Accessors.Select(a => Tokens(a.AccessorStatement)))));
                    break;
                case VisualBasicSyntax.PropertyStatementSyntax property:
                    output.Add(Property(property, container, string.Empty));
                    break;
                case VisualBasicSyntax.EventBlockSyntax @event:
                    output.Add(new(DeclarationKind.Member, container, @event.EventStatement.Identifier.ValueText, Tokens(@event.EventStatement), @event.EventStatement));
                    break;
                case VisualBasicSyntax.EventStatementSyntax @event:
                    output.Add(new(DeclarationKind.Member, container, @event.Identifier.ValueText, Tokens(@event), @event));
                    break;
                case VisualBasicSyntax.FieldDeclarationSyntax field:
                    {
                        var constant = field.Modifiers.Any(t => t.IsKind(VisualBasicKind.ConstKeyword));
                        var shared = string.Join(' ', [.. field.AttributeLists.Select(a => Tokens(a)), .. field.Modifiers.Select(t => t.Text)]);
                        foreach (var declarator in field.Declarators)
                        {
                            var type = declarator.AsClause is null ? string.Empty : " " + Tokens(declarator.AsClause);
                            var value = constant && declarator.Initializer is { } initializer ? " " + Tokens(initializer) : string.Empty;
                            foreach (var name in declarator.Names)
                            {
                                output.Add(new(DeclarationKind.Member, container, name.Identifier.ValueText, $"{shared} {Tokens(name)}{type}{value}", name));
                            }
                        }

                        break;
                    }

                default:
                    output.Add(new(DeclarationKind.Unknown, container, string.Empty, Tokens(member), member));
                    break;
            }
        }
    }

    // A default property (Default Property Item(i)) is read as obj(i): no name at the call site.
    private static Declaration Property(VisualBasicSyntax.PropertyStatementSyntax property, string container, string accessors)
    {
        var kind = property.Modifiers.Any(t => t.IsKind(VisualBasicKind.DefaultKeyword)) ? DeclarationKind.UnnamedMember : DeclarationKind.Member;
        var signature = Tokens(property, property.Initializer) + (accessors.Length > 0 ? " " + accessors : string.Empty);
        return new(kind, container, property.Identifier.ValueText, signature, property);
    }

    // Expression bodies, initializers and accessor bodies of a C# property, indexer, event or operator.
    private static SyntaxNode?[] Bodies(SyntaxNode member) =>
    [
        .. member.ChildNodes().OfType<CSharpSyntax.ArrowExpressionClauseSyntax>(),
        .. member.ChildNodes().OfType<CSharpSyntax.EqualsValueClauseSyntax>(),
        .. member.ChildNodes().OfType<CSharpSyntax.BlockSyntax>(),
        .. member.ChildNodes().OfType<CSharpSyntax.AccessorListSyntax>().SelectMany(list => list.Accessors).SelectMany(a => new SyntaxNode?[] { a.Body, a.ExpressionBody }),
    ];

    /// <summary>The node's tokens separated by single spaces: comments, layout and the excluded subtrees leave no trace.</summary>
    private static string Tokens(SyntaxNode node, params SyntaxNode?[] excluded)
    {
        var skip = excluded.OfType<SyntaxNode>().ToHashSet();
        var text = new StringBuilder();
        foreach (var token in node.DescendantTokens(n => !skip.Contains(n)))
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(token.Text);
        }

        return text.ToString();
    }

    private static string Arity(int? count) => count is > 0 ? $"`{count}" : string.Empty;

    private static string Join(string container, string name) => container.Length == 0 ? name : $"{container}.{name}";

    private static string TypeName(string container)
    {
        var name = container[(container.LastIndexOf('.') + 1)..];
        var arity = name.IndexOf('`', StringComparison.Ordinal);
        return arity >= 0 ? name[..arity] : name;
    }
}

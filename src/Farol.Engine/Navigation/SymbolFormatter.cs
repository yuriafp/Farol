using System.Text;
using System.Xml;
using System.Xml.Linq;
using Farol.Core.Paths;
using Microsoft.CodeAnalysis;

namespace Farol.Engine.Navigation;

/// <summary>
/// Renders symbols compactly and in the symbol's own language: a VB member shows VB syntax,
/// a C# member shows C# syntax.
/// </summary>
public static class SymbolFormatter
{
    /// <summary>Qualified by containing type (not namespace), with parameters and types: good for one-line results.</summary>
    public static readonly SymbolDisplayFormat Signature = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeRef
            | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeDefaultValue,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>Like <see cref="Signature"/> but without the containing type: for members listed under their type.</summary>
    public static readonly SymbolDisplayFormat Member = Signature.RemoveMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType);

    public static string Kind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
        INamedTypeSymbol { IsRecord: true } => "record",
        INamedTypeSymbol type => type.TypeKind switch
        {
            TypeKind.Class => "class",
            TypeKind.Interface => "interface",
            TypeKind.Struct => "struct",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            TypeKind.Module => "module",
            _ => "type",
        },
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "constructor",
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => "operator",
        IMethodSymbol => "method",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "enum member",
        IFieldSymbol { IsConst: true } => "const",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        INamespaceSymbol => "namespace",
        _ => symbol.Kind.ToString().ToUpperInvariant(),
    };

    /// <summary>Stable identity across calls and target frameworks: the documentation comment ID.</summary>
    public static string Id(ISymbol symbol)
    {
        var id = DocumentationCommentId.CreateDeclarationId(symbol);
        if (id is null)
        {
            return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        return symbol is IMethodSymbol { MethodKind: MethodKind.Conversion } ? id : NormalizeId(id);
    }

    /// <summary>
    /// Roslyn 5.x appends "~ReturnType" to every method ID; the documentation-comment standard (and the IDs in
    /// XML documentation files) only use it for conversion operators. Shorter, familiar IDs cost fewer tokens.
    /// </summary>
    public static string NormalizeId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var tilde = id.IndexOf('~', StringComparison.Ordinal);
        var isConversion = id.Contains(".op_Implicit(", StringComparison.Ordinal) || id.Contains(".op_Explicit(", StringComparison.Ordinal);
        return tilde > 0 && !isConversion ? id[..tilde] : id;
    }

    public static string Display(ISymbol symbol) => $"{Kind(symbol)} {symbol.ToDisplayString(Signature)}";

    public static string Namespace(ISymbol symbol) =>
        symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : string.Empty;

    /// <summary>"path:line" of the first source declaration, or "metadata: Assembly" for external symbols.</summary>
    public static string Location(ISymbol symbol, string root) =>
        SourceLocation(symbol) is { } location
            ? DisplayPath.Location(root, location.Path, location.Line)
            : $"metadata: {symbol.ContainingAssembly?.Name ?? "unknown"}";

    public static (string Path, int Line)? SourceLocation(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource && l.SourceTree?.FilePath is { Length: > 0 });
        return location is null ? null : (location.SourceTree!.FilePath, location.GetLineSpan().StartLinePosition.Line + 1);
    }

    /// <summary>The XML documentation summary as plain text, with cref references reduced to their short names.</summary>
    public static string? Summary(ISymbol symbol, CancellationToken cancellationToken)
    {
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var summary = XDocument.Parse(xml).Descendants("summary").FirstOrDefault();
            if (summary is null)
            {
                return null;
            }

            var text = new StringBuilder();
            foreach (var node in summary.DescendantNodes())
            {
                switch (node)
                {
                    case XText value:
                        text.Append(value.Value);
                        break;
                    case XElement { Name.LocalName: "see" or "seealso" or "paramref" or "typeparamref" } reference when !reference.Nodes().Any():
                        text.Append(ShortName((string?)reference.Attribute("cref") ?? (string?)reference.Attribute("name") ?? (string?)reference.Attribute("langword") ?? string.Empty));
                        break;
                }
            }

            var collapsed = string.Join(' ', text.ToString().Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
            return collapsed.Length == 0 ? null : collapsed;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string ShortName(string cref)
    {
        var withoutPrefix = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var withoutParameters = withoutPrefix.Split('(')[0];
        return withoutParameters[(withoutParameters.LastIndexOf('.') + 1)..];
    }
}

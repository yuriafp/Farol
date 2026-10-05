using Farol.Engine.Navigation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>
/// How dotnet_find_references classifies each use: types and calls from the syntax alone, which spares binding the code
/// around thousands of uses, and the rest (reads, writes, method groups, VB calls without parentheses) from the binding.
/// </summary>
public sealed class ReferenceKindTests
{
    private const string Shop = """
        namespace Shop
        {
            public class Price
            {
                public static Price Zero => new Price();
                public decimal Amount { get; set; }
            }

            public static class Prices
            {
                public static T Get<T>() => default!;
            }

            public class Cart
            {
                public Price Total(Price extra)
                {
                    var created = new Shop.Price();
                    var zero = Price.Zero;
                    var got = Prices.Get<Price>();
                    created.Amount = 1;
                    var amount = created.Amount;
                    System.Func<Price, Price> later = Total;
                    Total(created);
                    this?.Total(zero);
                    return got;
                }
            }
        }
        """;

    private const string Basket = """
        Namespace VbShop
            Public Class Basket
                Public Function Count() As Integer
                    Return 0
                End Function

                Public Sub Use()
                    Dim created As New Basket()
                    Dim one = created.Count()
                    Dim two = Count()
                    Dim three = created.Count
                    Dim none As Basket = Nothing
                End Sub
            End Class
        End Namespace
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_type_is_created_or_referenced_even_as_a_receiver_or_a_type_argument()
    {
        using var workspace = new AdhocWorkspace();
        var document = CSharpShop(workspace);
        var price = await DeclaredAsync(document, "Price", SymbolKind.NamedType);

        var kinds = await KindsAsync(document.Project.Solution, price);

        Assert.Equal(
            [
                "new: public static Price Zero => new Price();",
                "new: var created = new Shop.Price();",
                "reference: System.Func<Price, Price> later = Total;",
                "reference: System.Func<Price, Price> later = Total;",
                "reference: public Price Total(Price extra)",
                "reference: public Price Total(Price extra)",
                "reference: public static Price Zero => new Price();",
                "reference: var got = Prices.Get<Price>();",
                "reference: var zero = Price.Zero;",
            ],
            kinds);
    }

    [Fact]
    public async Task A_method_is_called_or_taken_as_a_method_group_and_a_property_read_or_written()
    {
        using var workspace = new AdhocWorkspace();
        var document = CSharpShop(workspace);
        var total = await DeclaredAsync(document, "Total", SymbolKind.Method);
        var amount = await DeclaredAsync(document, "Amount", SymbolKind.Property);

        Assert.Equal(["call: Total(created);", "call: this?.Total(zero);", "method group: System.Func<Price, Price> later = Total;"], await KindsAsync(document.Project.Solution, total));
        Assert.Equal(["read: var amount = created.Amount;", "write: created.Amount = 1;"], await KindsAsync(document.Project.Solution, amount));
    }

    [Fact]
    public async Task VB_calls_with_and_without_parentheses_and_New_are_classified()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "VbShop", "VbShop", LanguageNames.VisualBasic, metadataReferences: CoreLibrary()));
        var document = workspace.AddDocument(project.Id, "Basket.vb", SourceText.From(Basket));
        var basket = await DeclaredAsync(document, "Basket", SymbolKind.NamedType);
        var count = await DeclaredAsync(document, "Count", SymbolKind.Method);

        Assert.Equal(["new: Dim created As New Basket()", "reference: Dim none As Basket = Nothing"], await KindsAsync(document.Project.Solution, basket));
        Assert.Equal(["call: Dim one = created.Count()", "call: Dim three = created.Count", "call: Dim two = Count()"], await KindsAsync(document.Project.Solution, count));
    }

    private static Document CSharpShop(AdhocWorkspace workspace)
    {
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "Shop", "Shop", LanguageNames.CSharp, metadataReferences: CoreLibrary()));
        return workspace.AddDocument(project.Id, "Shop.cs", SourceText.From(Shop));
    }

    private static MetadataReference[] CoreLibrary() => [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];

    private static async Task<ISymbol> DeclaredAsync(Document document, string name, SymbolKind kind)
    {
        var root = await document.GetSyntaxRootAsync(Ct);
        var model = await document.GetSemanticModelAsync(Ct);
        return root!.DescendantNodes()
            .Select(node => model!.GetDeclaredSymbol(node, Ct))
            .First(symbol => symbol?.Name == name && symbol.Kind == kind)!;
    }

    /// <summary>Each use of the symbol as "kind: line", classified the way <see cref="ReferenceFinder"/> does it.</summary>
    private static async Task<string[]> KindsAsync(Solution solution, ISymbol symbol)
    {
        var kinds = new List<string>();
        var found = await SymbolFinder.FindReferencesAsync(symbol, solution, Ct);
        foreach (var (location, referenced) in ReferenceFinder.UsesOf(found, SymbolFormatter.Id(symbol)))
        {
            var root = await location.Document.GetSyntaxRootAsync(Ct);
            var model = await location.Document.GetSemanticModelAsync(Ct);
            var node = root!.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            var kind = ReferenceFinder.KindFromSyntax(referenced, node) ?? ReferenceFinder.Classify(node, model!, Ct);
            var text = await location.Document.GetTextAsync(Ct);
            kinds.Add($"{kind}: {text.Lines.GetLineFromPosition(location.Location.SourceSpan.Start).ToString().Trim()}");
        }

        return [.. kinds.Order(StringComparer.Ordinal)];
    }
}

using Farol.Engine.Navigation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>
/// How dotnet_find_references classifies each use: types and calls from the syntax alone, which spares binding the code
/// around thousands of uses, and the rest (reads, writes, method groups, VB calls without parentheses) from the binding.
/// A member's uses include those through the interface and base members a call reaches it through (AC-37).
/// </summary>
public sealed class ReferenceKindTests
{
    private const string Modules = """
        namespace Modules
        {
            public interface IModuleController
            {
                int GetModule(int id, bool ignoreCache);
            }

            public abstract class ControllerBase
            {
                public abstract string Describe();
            }

            public class ModuleController : ControllerBase, IModuleController
            {
                public static IModuleController Instance { get; } = new ModuleController();

                public int GetModule(int id, bool ignoreCache) => id;

                public override string Describe() => "modules";

                public override string ToString() => this.Describe();
            }

            public class FakeModuleController : IModuleController
            {
                public int GetModule(int id, bool ignoreCache) => 0;
            }

            public static class Callers
            {
                public static void Use(ControllerBase described, FakeModuleController fake)
                {
                    var shared = ModuleController.Instance.GetModule(1, false);
                    var direct = new ModuleController().GetModule(2, true);
                    var other = fake.GetModule(3, false);
                    var text = described.Describe();
                    var name = described.ToString();
                }
            }
        }
        """;

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

    [Fact]
    public async Task A_member_is_used_through_the_interface_member_it_implements_but_not_through_its_other_implementations()
    {
        using var workspace = new AdhocWorkspace();
        var document = CSharpDocument(workspace, Modules);
        var getModule = await DeclaredAsync(document, "GetModule", SymbolKind.Method, "ModuleController");
        var contract = await DeclaredAsync(document, "GetModule", SymbolKind.Method, "IModuleController");

        Assert.Equal(
            ["call via IModuleController: var shared = ModuleController.Instance.GetModule(1, false);", "call: var direct = new ModuleController().GetModule(2, true);"],
            await KindsAsync(document.Project.Solution, getModule));
        Assert.Equal(["call: var shared = ModuleController.Instance.GetModule(1, false);"], await KindsAsync(document.Project.Solution, contract));
    }

    [Fact]
    public async Task An_override_is_called_through_the_member_it_overrides_unless_a_referenced_assembly_declares_it()
    {
        using var workspace = new AdhocWorkspace();
        var document = CSharpDocument(workspace, Modules);
        var describe = await DeclaredAsync(document, "Describe", SymbolKind.Method, "ModuleController");
        var toString = await DeclaredAsync(document, "ToString", SymbolKind.Method, "ModuleController");

        // A call on the base type reaches the override through the member it overrides; one on the class itself is its own.
        Assert.Equal(
            ["call via ControllerBase: var text = described.Describe();", "call: public override string ToString() => this.Describe();"],
            await KindsAsync(document.Project.Solution, describe));
        Assert.Empty(await KindsAsync(document.Project.Solution, toString));
    }

    private static Document CSharpShop(AdhocWorkspace workspace) => CSharpDocument(workspace, Shop);

    private static Document CSharpDocument(AdhocWorkspace workspace, string source)
    {
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "Shop", "Shop", LanguageNames.CSharp, metadataReferences: CoreLibrary()));
        return workspace.AddDocument(project.Id, "Shop.cs", SourceText.From(source));
    }

    private static MetadataReference[] CoreLibrary() => [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];

    private static async Task<ISymbol> DeclaredAsync(Document document, string name, SymbolKind kind, string? containingType = null)
    {
        var root = await document.GetSyntaxRootAsync(Ct);
        var model = await document.GetSemanticModelAsync(Ct);
        return root!.DescendantNodes()
            .Select(node => model!.GetDeclaredSymbol(node, Ct))
            .First(symbol => symbol?.Name == name && symbol.Kind == kind && (containingType is null || symbol.ContainingType?.Name == containingType))!;
    }

    /// <summary>Each use of the symbol as "kind: line" ("kind via Type: line" through another member), as <see cref="ReferenceFinder"/> reports it.</summary>
    private static async Task<string[]> KindsAsync(Solution solution, ISymbol symbol)
    {
        var kinds = new List<string>();
        var (found, through) = await ReferenceFinder.SearchAsync(symbol, solution, Ct);
        foreach (var (location, referenced, via) in ReferenceFinder.UsesOf(found, SymbolFormatter.Id(symbol), through))
        {
            var root = await location.Document.GetSyntaxRootAsync(Ct);
            var model = await location.Document.GetSemanticModelAsync(Ct);
            var node = root!.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            var kind = ReferenceFinder.KindFromSyntax(referenced, node) ?? ReferenceFinder.Classify(node, model!, Ct);
            var text = await location.Document.GetTextAsync(Ct);
            kinds.Add($"{kind}{(via is null ? string.Empty : $" via {via}")}: {text.Lines.GetLineFromPosition(location.Location.SourceSpan.Start).ToString().Trim()}");
        }

        return [.. kinds.Order(StringComparer.Ordinal)];
    }
}

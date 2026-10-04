using Farol.Engine.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>What dotnet_check compares between two versions of a file to bound what an edit can break.</summary>
public sealed class DeclarationShapeTests
{
    private const string Calculator = """
        using System;

        namespace Shop
        {
            public class OrderCalculator
            {
                private const decimal Rate = 0.1m;
                private int _count = 1;

                public decimal GetTotal(int orderId)
                {
                    return orderId * Rate;
                }

                public int Count => _count;

                public static OrderCalculator operator +(OrderCalculator a, OrderCalculator b) => a;
            }
        }
        """;

    [Fact]
    public void Bodies_comments_and_layout_leave_the_shape_unchanged()
    {
        var edited = Calculator
            .Replace("return orderId * Rate;", "// cheaper\n            return orderId * Rate * 2;", StringComparison.Ordinal)
            .Replace("private int _count = 1;", "private int _count = 2;", StringComparison.Ordinal)
            .Replace("public int Count => _count;", "public int Count => _count + 1;", StringComparison.Ordinal);

        var (removed, added) = Diff(Calculator, edited);

        Assert.Empty(removed);
        Assert.Empty(added);
    }

    [Fact]
    public void A_new_parameter_changes_the_member_and_a_new_method_is_added()
    {
        var edited = Calculator
            .Replace("public decimal GetTotal(int orderId)", "public decimal GetTotal(int orderId, bool includeTax)", StringComparison.Ordinal)
            .Replace("public int Count", "public void Reset() { }\n\n        public int Count", StringComparison.Ordinal);

        var (removed, added) = Diff(Calculator, edited);

        var old = Assert.Single(removed);
        Assert.Equal((DeclarationKind.Member, "Shop.OrderCalculator", "GetTotal"), (old.Kind, old.Container, old.Name));
        Assert.Equal(["GetTotal", "Reset"], added.Select(d => d.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Constants_global_usings_type_headers_and_operators_have_their_own_reach()
    {
        var edited = "global using System.Linq;\n" + Calculator
            .Replace("private const decimal Rate = 0.1m;", "private const decimal Rate = 0.2m;", StringComparison.Ordinal)
            .Replace("public class OrderCalculator", "public sealed class OrderCalculator", StringComparison.Ordinal)
            .Replace("operator +(", "operator -(", StringComparison.Ordinal);

        var (removed, added) = Diff(Calculator, edited);

        Assert.Contains(added, d => d.Kind == DeclarationKind.ProjectWide);
        Assert.Contains(removed, d => d is { Kind: DeclarationKind.Member, Name: "Rate" });
        Assert.Contains(added, d => d is { Kind: DeclarationKind.Type, Name: "OrderCalculator" });
        Assert.Contains(removed, d => d is { Kind: DeclarationKind.Type, Name: "OrderCalculator" });
        Assert.Contains(added, d => d.Kind == DeclarationKind.UnnamedMember);
    }

    [Fact]
    public void Visual_basic_signatures_constants_and_default_properties()
    {
        const string Shipping = """
            Namespace Shop
                Public Class ShippingCalculator
                    Private Const Base As Decimal = 5D

                    Public Function Cost(ByVal weight As Decimal) As Decimal
                        Return Base + weight
                    End Function

                    Default Public ReadOnly Property Item(ByVal index As Integer) As Decimal
                        Get
                            Return index
                        End Get
                    End Property
                End Class
            End Namespace
            """;
        var edited = Shipping
            .Replace("Return Base + weight", "Return Base + weight * 2D", StringComparison.Ordinal)
            .Replace("Cost(ByVal weight As Decimal)", "Cost(ByVal weight As Decimal, ByVal express As Boolean)", StringComparison.Ordinal)
            .Replace("Private Const Base As Decimal = 5D", "Private Const Base As Decimal = 6D", StringComparison.Ordinal);

        var before = DeclarationShape.Of(VisualBasicSyntaxTree.ParseText(Shipping, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken));
        var (removed, added) = Diff(Shipping, edited, LanguageNames.VisualBasic);

        Assert.Contains(before, d => d is { Kind: DeclarationKind.UnnamedMember, Name: "Item" });
        Assert.Equal(["Base", "Cost"], removed.Select(d => d.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["Base", "Cost"], added.Select(d => d.Name).Order(StringComparer.Ordinal));
        Assert.All(removed, d => Assert.Equal("Shop.ShippingCalculator", d.Container));
    }

    private static (List<Declaration> Removed, List<Declaration> Added) Diff(string before, string after, string language = LanguageNames.CSharp)
    {
        var old = Shape(before, language);
        var updated = Shape(after, language);
        var oldKeys = old.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var newKeys = updated.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        return ([.. old.Where(d => !newKeys.Contains(d.Key))], [.. updated.Where(d => !oldKeys.Contains(d.Key))]);
    }

    private static IReadOnlyList<Declaration> Shape(string text, string language)
    {
        var ct = TestContext.Current.CancellationToken;
        var tree = language == LanguageNames.CSharp
            ? CSharpSyntaxTree.ParseText(text, cancellationToken: ct)
            : VisualBasicSyntaxTree.ParseText(text, cancellationToken: ct);
        return DeclarationShape.Of(tree.GetRoot(ct));
    }
}

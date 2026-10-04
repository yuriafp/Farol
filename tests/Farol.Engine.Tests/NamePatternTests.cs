using Farol.Engine.Navigation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>The patterns of dotnet_find_symbols, matched against the declaration table without creating symbols.</summary>
public sealed class NamePatternTests
{
    [Theory]
    [InlineData("OrdCalc", "OrderCalculator", true)]
    [InlineData("OCalc", "OrderCalculator", true)]
    [InlineData("calculator", "OrderCalculator", true)]
    [InlineData("OrderCalculator", "OrderCalculator", true)]
    [InlineData("UmbConAcc", "IUmbracoContextAccessor", true)]
    [InlineData("CalcOrd", "OrderCalculator", false)]
    [InlineData("OrdCalx", "OrderCalculator", false)]
    [InlineData("GetTot", "get_total", true)]
    [InlineData("SerObj", "SerializeObject", true)]
    public void Names_match_by_substring_or_camel_humps(string query, string name, bool expected) =>
        Assert.Equal(expected, NamePattern.Create(query).MatchesName(name));

    [Fact]
    public void A_dotted_query_also_matches_the_containers()
    {
        var tree = CSharpSyntaxTree.ParseText(
            "namespace Shop.Orders { class OrderCalculator { int GetTotal() => 0; } class Cart { int GetTotal() => 1; } }",
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("Shop", [tree]);
        var totals = compilation.GetSymbolsWithName("GetTotal", SymbolFilter.Member, TestContext.Current.CancellationToken).ToList();

        var pattern = NamePattern.Create("OrdCalc.GetTotal");

        Assert.Equal(2, totals.Count);
        Assert.Equal(["OrderCalculator"], totals.Where(pattern.MatchesContainers).Select(s => s.ContainingType.Name));
        Assert.True(NamePattern.Create("Orders.OrderCalculator.GetTotal").MatchesContainers(totals.First(s => s.ContainingType.Name == "OrderCalculator")));
    }
}

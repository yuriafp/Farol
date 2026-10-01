using Microsoft.CodeAnalysis;

namespace Farol.Engine.Testing;

/// <summary>
/// The test frameworks Farol knows (xUnit, NUnit, MSTest, TUnit): recognized by assembly name, package id or project
/// SDK, and the attributes that make a method a test.
/// </summary>
public static class TestFrameworks
{
    public const string XUnit = "xUnit";
    public const string NUnit = "NUnit";
    public const string MSTest = "MSTest";
    public const string TUnit = "TUnit";

    // Attribute types that mark a test, by namespace. Custom attributes deriving from them count too.
    private static readonly Dictionary<string, string[]> TestAttributes = new(StringComparer.Ordinal)
    {
        ["Xunit"] = ["FactAttribute", "TheoryAttribute"],
        ["NUnit.Framework"] = ["TestAttribute", "TestCaseAttribute", "TestCaseSourceAttribute", "TheoryAttribute"],
        ["Microsoft.VisualStudio.TestTools.UnitTesting"] = ["TestMethodAttribute", "DataTestMethodAttribute"],
        ["TUnit.Core"] = ["TestAttribute"],
    };

    // Before a restore the framework's attributes do not resolve, but their names still say what they are.
    private static readonly HashSet<string> UnresolvedTestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "FactAttribute", "Theory", "TheoryAttribute", "Test", "TestAttribute", "TestCase", "TestCaseAttribute",
        "TestCaseSource", "TestCaseSourceAttribute", "TestMethod", "TestMethodAttribute", "DataTestMethod", "DataTestMethodAttribute",
    };

    /// <summary>Frameworks named by references (assembly names or package ids) or by the project SDK.</summary>
    public static IReadOnlyList<string> Detect(IEnumerable<string> references, string? sdk = null)
    {
        var names = references.ToList();
        var found = new List<string>();
        if (names.Any(n => n.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)))
        {
            found.Add(XUnit);
        }

        if (names.Any(n => n.StartsWith("nunit", StringComparison.OrdinalIgnoreCase)))
        {
            found.Add(NUnit);
        }

        if (names.Any(n => n.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase)
                           || n.Equals("Microsoft.VisualStudio.TestPlatform.TestFramework", StringComparison.OrdinalIgnoreCase)
                           || n.Equals("Microsoft.VisualStudio.QualityTools.UnitTestFramework", StringComparison.OrdinalIgnoreCase))
            || sdk?.StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase) == true)
        {
            found.Add(MSTest);
        }

        if (names.Any(n => n.StartsWith("TUnit", StringComparison.OrdinalIgnoreCase)))
        {
            found.Add(TUnit);
        }

        return found;
    }

    public static bool IsTest(IMethodSymbol method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method.GetAttributes().Any(a => a.AttributeClass is { } type && IsTestAttribute(type));
    }

    /// <summary>
    /// The name test runners filter on (VSTest's FullyQualifiedName): namespace, type and method, with '+' between
    /// nested types — "Ns.Outer+Inner.Method". Data-driven tests share their method's name.
    /// </summary>
    public static string FullyQualifiedName(IMethodSymbol method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var type = method.ContainingType;
        var typeName = type.MetadataName;
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            typeName = outer.MetadataName + "+" + typeName;
        }

        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() + "." : string.Empty;
        return ns + typeName + "." + method.Name;
    }

    private static bool IsTestAttribute(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Error)
        {
            return UnresolvedTestAttributes.Contains(type.Name);
        }

        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (TestAttributes.TryGetValue(current.ContainingNamespace.ToDisplayString(), out var names) && names.Contains(current.Name, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

using Microsoft.CodeAnalysis;

namespace Greeting.Generator;

/// <summary>Adds Greeting.Generated, which the app uses: it compiles only when this generator runs.</summary>
[Generator]
public sealed class GreetingGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterPostInitializationOutput(output => output.AddSource(
            "Generated.g.cs",
            "namespace Greeting;\n\npublic static class Generated\n{\n    public const string Text = \"Hello\";\n}\n"));
}

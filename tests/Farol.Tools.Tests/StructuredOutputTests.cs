using System.Text.Json;
using Farol.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>AC-68: every tool declares an output schema, and its structured content matches it.</summary>
public sealed class StructuredOutputTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC68_every_tool_declares_an_output_schema()
    {
        await using var harness = await McpHarness.StartAsync(TestPaths.ModernDirectory, Ct);
        var tools = await harness.Client.ListToolsAsync(cancellationToken: Ct);

        Assert.All(tools, t => Assert.True(t.ProtocolTool.OutputSchema is { ValueKind: JsonValueKind.Object }, $"{t.Name} has no output schema."));
    }

    [Theory]
    [MemberData(nameof(ModernCalls))]
    public async Task AC68_structured_content_matches_the_output_schema_on_the_modern_fixture(string tool, string argumentsJson)
    {
        using var copy = FixtureCopy.Create(TestPaths.ModernDirectory);
        await FixtureRestore.EnsureRestoredAsync(copy.PathOf("Modern.slnx"), Ct);
        await using var harness = await McpHarness.StartAsync(copy.Root, Ct);
        await harness.CallAsync("dotnet_workspace", new() { ["action"] = "load" }, Ct);

        await AssertMatchesAsync(harness, tool, argumentsJson);
    }

    [Theory]
    [MemberData(nameof(LegacyCalls))]
    public async Task AC68_structured_content_matches_the_output_schema_on_the_legacy_fixture(string tool, string argumentsJson)
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");

        await AssertMatchesAsync(server.Harness!, tool, argumentsJson);
    }

    private static async Task AssertMatchesAsync(McpHarness harness, string tool, string argumentsJson)
    {
        var listed = (await harness.Client.ListToolsAsync(cancellationToken: Ct)).Single(t => t.Name == tool);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)!;
        var result = await harness.Client.CallToolAsync(tool, arguments, cancellationToken: Ct);

        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.False(result.IsError is true, text);
        Assert.NotNull(result.StructuredContent);
        var problems = SchemaCheck.Validate(listed.ProtocolTool.OutputSchema!.Value, result.StructuredContent!.Value);
        Assert.True(problems.Count == 0, $"{tool}: {string.Join("; ", problems)}\n{result.StructuredContent}");
    }

    private const string ModernCalculator = "src/Modern.Core/Pricing/PriceCalculator.cs";

    public static TheoryData<string, string> ModernCalls() => new()
    {
        { "dotnet_overview", "{}" },
        { "dotnet_workspace", "{}" },
        { "dotnet_find_symbols", """{"query":"PriceCalculator"}""" },
        { "dotnet_find_references", """{"symbol":"IClock"}""" },
        { "dotnet_find_references", """{"symbol":"UtcNow"}""" },
        { "dotnet_hierarchy", """{"symbol":"IClock","direction":"implementations"}""" },
        { "dotnet_call_hierarchy", """{"symbol":"PriceCalculator.Total"}""" },
        { "dotnet_outline", $$"""{"path":"{{ModernCalculator}}"}""" },
        { "dotnet_symbol", """{"symbol":"PriceCalculator.Total","include":"signature,docs,source"}""" },
        { "dotnet_check", """{"scope":"solution","includeExisting":true}""" },
        { "dotnet_check", $$"""{"format":"hook","edited":"{{ModernCalculator}}"}""" },
        { "dotnet_code_actions", $$"""{"path":"{{ModernCalculator}}","line":19}""" },
        { "dotnet_code_actions", $$"""{"path":"{{ModernCalculator}}","line":19,"action":"1"}""" },
        { "dotnet_build", """{"project":"Modern.Core"}""" },
        { "dotnet_test", """{"filter":"Pricing"}""" },
        { "dotnet_packages", """{"project":"Modern.Core"}""" },
        { "dotnet_package_api", """{"package":"Newtonsoft.Json","version":"12.0.3"}""" },
        { "dotnet_package_api", """{"package":"Newtonsoft.Json","version":"12.0.3","query":"JsonConvert"}""" },
        { "dotnet_package_api", """{"package":"Newtonsoft.Json","version":"12.0.3","query":"Serialize"}""" },
        { "dotnet_legacy_inventory", "{}" },
        { "dotnet_portability", "{}" },
        { "dotnet_migration_plan", "{}" },
        { "dotnet_config_inspect", "{}" },
    };

    public static TheoryData<string, string> LegacyCalls() => new()
    {
        { "dotnet_legacy_inventory", "{}" },
        { "dotnet_portability", "{}" },
        { "dotnet_migration_plan", "{}" },
        { "dotnet_config_inspect", "{}" },
        { "dotnet_config_inspect", """{"path":"Legacy.Web/Web.config"}""" },
        { "dotnet_packages", "{}" },
        { "dotnet_code_actions", """{"path":"Legacy.Core/Orders/OrderCalculator.cs","line":23}""" },
        { "dotnet_find_references", """{"symbol":"_Default.lblTotal","kinds":"markup"}""" },
        { "dotnet_rename", """{"symbol":"OrderCalculator","newName":"TotalCalculator"}""" },
        { "dotnet_rename", """{"symbol":"OrderCalculator.GetTotal","newName":"CalculateTotal","apply":true}""" },
        { "dotnet_change_signature", """{"symbol":"OrderCalculator.GetTotal","parameters":[{"name":"discount","type":"decimal","value":"0"},{"name":"orderId"}]}""" },
        { "dotnet_move_type", """{"symbol":"OrderLine","toFile":"Legacy.Core/Orders/OrderLine.cs"}""" },
        { "dotnet_convert_project", """{"project":"Legacy.Core","kind":"sdk_style"}""" },
        { "dotnet_extract_method", """{"path":"Legacy.Core/Orders/OrderCalculator.cs","startLine":17,"endLine":21,"name":"LoadOrder"}""" },
    };
}

/// <summary>
/// Checks a JSON value against the subset of JSON Schema that System.Text.Json's exporter writes: type (or a list of
/// types), properties, required, items, enum and local $ref.
/// </summary>
internal static class SchemaCheck
{
    public static List<string> Validate(JsonElement schema, JsonElement value)
    {
        var problems = new List<string>();
        Check(schema, schema, value, "$", problems);
        return problems;
    }

    private static void Check(JsonElement root, JsonElement schema, JsonElement value, string path, List<string> problems)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("$ref", out var reference))
        {
            // The exporter points a repeated type at its first use: "#/properties/a/items/properties/b".
            var target = root;
            foreach (var segment in reference.GetString()!.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!target.TryGetProperty(segment, out target))
                {
                    problems.Add($"{path}: unresolved $ref {reference}");
                    return;
                }
            }

            Check(root, target, value, path, problems);
            return;
        }

        if (schema.TryGetProperty("type", out var type))
        {
            var allowed = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!).ToList() : [type.GetString()!];
            if (!allowed.Any(t => Matches(t, value)))
            {
                problems.Add($"{path}: {value.ValueKind} is not {string.Join("|", allowed)}");
                return;
            }
        }

        if (schema.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(v => v.ToString() == value.ToString()))
        {
            problems.Add($"{path}: '{value}' is not one of {values}");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                {
                    if (!value.TryGetProperty(name, out _))
                    {
                        problems.Add($"{path}.{name}: required but missing");
                    }
                }
            }

            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (properties.TryGetProperty(property.Name, out var propertySchema))
                    {
                        Check(root, propertySchema, property.Value, $"{path}.{property.Name}", problems);
                    }
                    else
                    {
                        problems.Add($"{path}.{property.Name}: not in the schema");
                    }
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                Check(root, items, item, $"{path}[{index++}]", problems);
            }
        }
    }

    private static bool Matches(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };
}

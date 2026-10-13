using System.Globalization;
using System.Text.Json;
using Farol.Core;
using Farol.Core.Usage;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Infrastructure;

/// <summary>
/// Checks a call's arguments against the tool's input schema before the SDK binds them. The SDK answers a missing
/// argument or one of the wrong JSON type (an object where a string goes) with a bare "An error occurred invoking";
/// this answers with an <c>invalid_argument</c> error that names the argument. It refuses only what the SDK's binding
/// refuses too: a number written as a string binds, and so does null, which the tool then handles.
/// </summary>
internal sealed class ArgumentCheck : DelegatingMcpServerTool
{
    private const string Advice = "Pass each argument with the type the tool's input schema gives.";

    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _types = new(StringComparer.Ordinal);
    private readonly string[] _required = [];

    public ArgumentCheck(McpServerTool tool)
        : base(tool)
    {
        var schema = ProtocolTool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                _names.Add(property.Name);
                if (property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("type", out var type) && Types(type) is { Length: > 0 } types)
                {
                    _types[property.Name] = types;
                }
            }
        }

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            _required = [.. required.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!)];
        }
    }

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Problem(request.Params?.Arguments) is not { } problem)
        {
            return base.InvokeAsync(request, cancellationToken);
        }

        ToolCallOutcome.Current?.Fail(ErrorCodes.InvalidArgument);
        return ValueTask.FromResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = problem }] });
    }

    private string? Problem(IDictionary<string, JsonElement>? arguments)
    {
        arguments ??= new Dictionary<string, JsonElement>();
        if (_required.FirstOrDefault(name => !arguments.ContainsKey(name)) is { } missing)
        {
            var unknown = arguments.Keys.Where(name => !_names.Contains(name)).Select(name => $"'{name}'").ToList();
            return unknown.Count == 0
                ? $"{ProtocolTool.Name} needs '{missing}'."
                : $"{ProtocolTool.Name} needs '{missing}'; it does not take {string.Join(", ", unknown)}.";
        }

        foreach (var (name, value) in arguments)
        {
            if (_types.TryGetValue(name, out var types) && !types.Any(type => Fits(type, value)))
            {
                return $"'{name}' takes {Describe(types)}, not {Describe(value)}. {Advice}";
            }
        }

        return null;
    }

    private static string[] Types(JsonElement type) => type.ValueKind switch
    {
        JsonValueKind.String => [type.GetString()!],
        JsonValueKind.Array => [.. type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!)],
        _ => [],
    };

    // Null is left to the SDK and the tool: optional strings accept it.
    private static bool Fits(string type, JsonElement value) => value.ValueKind == JsonValueKind.Null || type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number ? value.TryGetInt64(out _) : value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        "number" => value.ValueKind == JsonValueKind.Number || (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => false,
        _ => true,
    };

    private static string Describe(string[] types) =>
        string.Join(" or ", types.Where(t => t != "null").Select(t => t switch
        {
            "integer" => "a whole number",
            "array" or "object" => $"an {t}",
            _ => $"a {t}",
        }));

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => value.GetRawText() is { Length: <= 24 } number ? number : "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };
}

using System.Text.Json;
using Farol.Core;
using Farol.Core.Usage;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Farol.Tools.Infrastructure;

/// <summary>
/// Checks a call's arguments against the tool's input schema before the SDK binds them. The SDK answers a missing
/// argument or one of the wrong JSON type (an object where a string goes) with a bare "An error occurred invoking";
/// this answers with an <c>invalid_argument</c> error that names the argument.
/// </summary>
internal sealed class ArgumentCheck : DelegatingMcpServerTool
{
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
                if (property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("type", out var type))
                {
                    _types[property.Name] = type.ValueKind == JsonValueKind.Array
                        ? [.. type.EnumerateArray().Select(t => t.GetString() ?? string.Empty)]
                        : [type.GetString() ?? string.Empty];
                }
            }
        }

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            _required = [.. required.EnumerateArray().Select(r => r.GetString() ?? string.Empty)];
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
        return ValueTask.FromResult(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"{problem} Pass each argument with the type the tool's input schema gives." }],
        });
    }

    private string? Problem(IDictionary<string, JsonElement>? arguments)
    {
        arguments ??= new Dictionary<string, JsonElement>();
        if (_required.FirstOrDefault(name => !arguments.ContainsKey(name)) is { } missing)
        {
            var unknown = arguments.Keys.Where(name => !_types.ContainsKey(name)).Select(name => $"'{name}'").ToList();
            return unknown.Count == 0
                ? $"{ProtocolTool.Name} needs '{missing}'."
                : $"{ProtocolTool.Name} needs '{missing}'; it does not take {string.Join(", ", unknown)}.";
        }

        foreach (var (name, value) in arguments)
        {
            if (_types.TryGetValue(name, out var types) && !types.Any(type => Fits(type, value)))
            {
                return $"'{name}' takes {Describe(types)}, not {Describe(value)}.";
            }
        }

        return null;
    }

    private static bool Fits(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
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
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };
}

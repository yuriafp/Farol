using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farol.Core.Text;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Farol.Tools.Infrastructure;

/// <summary>
/// A tool's answer: the text agents have read since Phase 1, unchanged, and the same facts as structured content that
/// matches the tool's output schema (AC-68). Lists carry the window the text shows, so both stay within one budget.
/// </summary>
internal sealed record ToolOutput<T>(string Text, T Data)
{
    // The schema lists every property as required, nullable ones included, so nulls are written rather than left out.
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public CallToolResult ToResult() => new()
    {
        Content = [new TextContentBlock { Text = Text }],
        StructuredContent = JsonSerializer.SerializeToElement(Data, Json),
    };
}

internal static class ToolOutput
{
    public static ToolOutput<T> Of<T>(string text, T data) => new(text, data);
}

/// <summary>The items of a list the text shows, with where they sit in the whole list.</summary>
public sealed record Page<T>(
    [property: Description("Items in the whole list.")] int Total,
    [property: Description("Index of the first item shown.")] int Offset,
    [property: Description("Index of the first item not shown, when the list was cut: the offset to pass for the rest where the tool takes one; otherwise raise maxTokens or narrow the call. Null when nothing was cut.")] int? NextOffset,
    IReadOnlyList<T> Items);

internal static class Page
{
    public static Page<TOut> From<TIn, TOut>(IReadOnlyList<TIn> items, ListWindow window, Func<TIn, TOut> map) =>
        new(window.Total, window.Start, window.NextOffset, [.. window.Of(items).Select(map)]);
}

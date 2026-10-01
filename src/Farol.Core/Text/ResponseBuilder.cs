using System.Text;

namespace Farol.Core.Text;

/// <summary>
/// Builds compact, line-oriented tool responses inside a token budget. Header lines are always
/// written; budgeted lines stop early and the response states what was left out and how to get it,
/// so a cut is never silent.
/// </summary>
public sealed class ResponseBuilder
{
    // Room kept for the "… N more" line that closes a truncated list.
    private const int TruncationReserve = 120;

    private readonly StringBuilder _text = new();
    private readonly int _maxChars;

    public ResponseBuilder(int maxTokens)
    {
        _maxChars = TokenBudget.Clamp(maxTokens) * TokenBudget.CharsPerToken;
    }

    public bool Truncated { get; private set; }

    /// <summary>Writes a line regardless of the budget (headers, summaries).</summary>
    public ResponseBuilder Line(string line = "")
    {
        _text.Append(line).Append('\n');
        return this;
    }

    /// <summary>Writes a line only if it fits the budget; returns false (and marks the response truncated) otherwise.</summary>
    public bool TryLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (_text.Length + line.Length + 1 > _maxChars - TruncationReserve)
        {
            Truncated = true;
            return false;
        }

        Line(line);
        return true;
    }

    /// <summary>Closes a cut list: "… N more (hint)".</summary>
    public ResponseBuilder More(int omitted, string? hint)
    {
        if (omitted > 0)
        {
            Truncated = true;
            Line(hint is null ? $"- … {omitted} more" : $"- … {omitted} more ({hint})");
        }

        return this;
    }

    /// <summary>
    /// Writes a titled list starting at <paramref name="offset"/> until the budget runs out.
    /// <paramref name="continuation"/> turns the next offset into a hint such as "call again with offset=40".
    /// </summary>
    public ResponseBuilder List<T>(string title, IReadOnlyList<T> items, Func<T, string> render, int offset = 0, Func<int, string>? continuation = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(render);

        var start = Math.Clamp(offset, 0, items.Count);
        Line(start > 0 ? $"{title} ({items.Count}, from #{start + 1}):" : $"{title} ({items.Count}):");

        var index = start;
        while (index < items.Count && TryLine("- " + render(items[index])))
        {
            index++;
        }

        return More(items.Count - index, continuation?.Invoke(index));
    }

    public override string ToString() => _text.ToString().TrimEnd('\n');

    /// <summary>The standard continuation hint for tools that take an offset.</summary>
    public static Func<int, string> OffsetHint { get; } = next => $"call again with offset={next}";
}

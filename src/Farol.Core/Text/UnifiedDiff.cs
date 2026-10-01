using System.Globalization;
using System.Text;

namespace Farol.Core.Text;

/// <summary>
/// Line-based unified diffs (Myers' algorithm, three lines of context): the format agents read best and
/// <c>git apply</c> accepts. The path is written as given, so callers pass it relative to the workspace root.
/// </summary>
public static class UnifiedDiff
{
    public const int DefaultContext = 3;

    // Past this many differing lines an exact edit script is not worth its cost: the changed middle is replaced.
    private const int MaxEditDistance = 2000;

    private const string NoNewLineMarker = "\\ No newline at end of file";

    /// <summary>Diffs two versions of a file; <c>null</c> stands for a file that does not exist (created or deleted).</summary>
    public static string Create(string path, string? oldText, string? newText, int context = DefaultContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var before = TextLines.From(oldText);
        var after = TextLines.From(newText);
        var edits = Edits(before.Keys, after.Keys);
        if (edits.TrueForAll(e => e.Kind == ' ') && (oldText is null) == (newText is null))
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.Append("--- ").Append(oldText is null ? "/dev/null" : "a/" + path).Append('\n');
        text.Append("+++ ").Append(newText is null ? "/dev/null" : "b/" + path).Append('\n');
        foreach (var (start, end) in HunkRanges(edits, Math.Max(0, context)))
        {
            AppendHunk(text, edits, start, end, before, after);
        }

        return text.ToString().TrimEnd('\n');
    }

    private static List<Edit> Edits(string[] a, string[] b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
        {
            suffix++;
        }

        var edits = new List<Edit>(Math.Max(a.Length, b.Length) + 8);
        for (var i = 0; i < prefix; i++)
        {
            edits.Add(new Edit(' ', i, i));
        }

        edits.AddRange(Middle(a, b, prefix, a.Length - suffix, b.Length - suffix));
        for (var i = 0; i < suffix; i++)
        {
            edits.Add(new Edit(' ', a.Length - suffix + i, b.Length - suffix + i));
        }

        return edits;
    }

    // Myers' O(ND) diff over a[start..aEnd) and b[start..bEnd), keeping each round's frontier to walk the path back.
    private static List<Edit> Middle(string[] a, string[] b, int start, int aEnd, int bEnd)
    {
        int n = aEnd - start, m = bEnd - start;
        if (n == 0 || m == 0)
        {
            return ReplaceAll(start, aEnd, bEnd);
        }

        var max = Math.Min(n + m, MaxEditDistance);
        var offset = max;
        var frontier = new int[(2 * max) + 2];
        var trace = new List<int[]>();
        for (var d = 0; d <= max; d++)
        {
            trace.Add((int[])frontier.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && frontier[offset + k - 1] < frontier[offset + k + 1])
                    ? frontier[offset + k + 1]
                    : frontier[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[start + x] == b[start + y])
                {
                    x++;
                    y++;
                }

                frontier[offset + k] = x;
                if (x >= n && y >= m)
                {
                    return Backtrack(trace, n, m, offset, start);
                }
            }
        }

        return ReplaceAll(start, aEnd, bEnd);
    }

    private static List<Edit> Backtrack(List<int[]> trace, int n, int m, int offset, int start)
    {
        var edits = new List<Edit>();
        int x = n, y = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var frontier = trace[d];
            var k = x - y;
            var previousK = k == -d || (k != d && frontier[offset + k - 1] < frontier[offset + k + 1]) ? k + 1 : k - 1;
            var previousX = frontier[offset + previousK];
            var previousY = previousX - previousK;
            while (x > previousX && y > previousY)
            {
                edits.Add(new Edit(' ', start + x - 1, start + y - 1));
                x--;
                y--;
            }

            if (d > 0)
            {
                edits.Add(x == previousX ? new Edit('+', -1, start + y - 1) : new Edit('-', start + x - 1, -1));
            }

            x = previousX;
            y = previousY;
        }

        edits.Reverse();
        return edits;
    }

    private static List<Edit> ReplaceAll(int start, int aEnd, int bEnd)
    {
        var edits = new List<Edit>((aEnd - start) + (bEnd - start));
        for (var i = start; i < aEnd; i++)
        {
            edits.Add(new Edit('-', i, -1));
        }

        for (var j = start; j < bEnd; j++)
        {
            edits.Add(new Edit('+', -1, j));
        }

        return edits;
    }

    // Changes closer than twice the context share one hunk.
    private static IEnumerable<(int Start, int End)> HunkRanges(List<Edit> edits, int context)
    {
        int? start = null;
        var lastChange = -1;
        for (var i = 0; i < edits.Count; i++)
        {
            if (edits[i].Kind == ' ')
            {
                continue;
            }

            if (start is null)
            {
                start = Math.Max(0, i - context);
            }
            else if (i - lastChange - 1 > 2 * context)
            {
                yield return (start.Value, Math.Min(edits.Count, lastChange + context + 1));
                start = Math.Max(0, i - context);
            }

            lastChange = i;
        }

        if (start is not null)
        {
            yield return (start.Value, Math.Min(edits.Count, lastChange + context + 1));
        }
    }

    private static void AppendHunk(StringBuilder text, List<Edit> edits, int start, int end, TextLines before, TextLines after)
    {
        int oldBefore = 0, newBefore = 0, oldCount = 0, newCount = 0;
        for (var i = 0; i < end; i++)
        {
            var kind = edits[i].Kind;
            if (i < start)
            {
                oldBefore += kind == '+' ? 0 : 1;
                newBefore += kind == '-' ? 0 : 1;
            }
            else
            {
                oldCount += kind == '+' ? 0 : 1;
                newCount += kind == '-' ? 0 : 1;
            }
        }

        // An empty side names the line it follows (0 at the top of the file), as diff and git do.
        var oldLine = oldCount == 0 ? oldBefore : oldBefore + 1;
        var newLine = newCount == 0 ? newBefore : newBefore + 1;
        text.Append(CultureInfo.InvariantCulture, $"@@ -{oldLine},{oldCount} +{newLine},{newCount} @@").Append('\n');

        for (var i = start; i < end; i++)
        {
            var edit = edits[i];
            switch (edit.Kind)
            {
                case ' ':
                    text.Append(' ').Append(before.Lines[edit.OldIndex]).Append('\n');
                    AppendMarker(text, before, edit.OldIndex);
                    break;
                case '-':
                    text.Append('-').Append(before.Lines[edit.OldIndex]).Append('\n');
                    AppendMarker(text, before, edit.OldIndex);
                    break;
                default:
                    text.Append('+').Append(after.Lines[edit.NewIndex]).Append('\n');
                    AppendMarker(text, after, edit.NewIndex);
                    break;
            }
        }
    }

    private static void AppendMarker(StringBuilder text, TextLines lines, int index)
    {
        if (!lines.EndsWithNewLine && index == lines.Lines.Length - 1)
        {
            text.Append(NoNewLineMarker).Append('\n');
        }
    }

    private readonly record struct Edit(char Kind, int OldIndex, int NewIndex);

    /// <summary>Lines without terminators; the comparison keys tell a last line without a newline from one with it.</summary>
    private sealed class TextLines
    {
        private TextLines(string[] lines, bool endsWithNewLine)
        {
            Lines = lines;
            EndsWithNewLine = endsWithNewLine;
            Keys = endsWithNewLine || lines.Length == 0 ? lines : [.. lines[..^1], lines[^1] + "\n\0"];
        }

        public string[] Lines { get; }

        public string[] Keys { get; }

        public bool EndsWithNewLine { get; }

        public static TextLines From(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new TextLines([], endsWithNewLine: true);
            }

            var lines = new List<string>();
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] is not ('\r' or '\n'))
                {
                    continue;
                }

                lines.Add(text[start..i]);
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }

            var endsWithNewLine = start == text.Length;
            if (!endsWithNewLine)
            {
                lines.Add(text[start..]);
            }

            return new TextLines([.. lines], endsWithNewLine);
        }
    }
}

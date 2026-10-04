using Microsoft.CodeAnalysis;

namespace Farol.Engine.Navigation;

/// <summary>
/// The patterns of IDE "go to symbol": a name matches when it contains the query (ignoring case) or when each
/// capitalized chunk of the query starts consecutive camel-case humps of the name ("OrdCalc" → OrderCalculator).
/// A dotted query ("OrderCalculator.GetTotal") also requires the containing types or namespaces to match, innermost last.
/// Built once per query: matching a name allocates nothing, so the whole declaration table can be scanned.
/// </summary>
internal sealed class NamePattern
{
    private readonly Segment _name;
    private readonly Segment[] _qualifier;

    private NamePattern(Segment name, Segment[] qualifier)
    {
        _name = name;
        _qualifier = qualifier;
    }

    public static NamePattern Create(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var segments = query.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length == 0
            ? new NamePattern(new Segment(query.Trim()), [])
            : new NamePattern(new Segment(segments[^1]), [.. segments[..^1].Select(s => new Segment(s))]);
    }

    public bool MatchesName(string name) => _name.Matches(name);

    /// <summary>The qualifier segments must match the symbol's containers, nearest container last.</summary>
    public bool MatchesContainers(ISymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var container = symbol.ContainingSymbol;
        for (var i = _qualifier.Length - 1; i >= 0; i--)
        {
            if (container is null or INamespaceSymbol { IsGlobalNamespace: true } || !_qualifier[i].Matches(container.Name))
            {
                return false;
            }

            container = container.ContainingSymbol;
        }

        return true;
    }

    private sealed class Segment
    {
        private readonly string _text;
        private readonly (int Start, int Length)[] _chunks;

        public Segment(string text)
        {
            _text = text;
            _chunks = [.. Humps(text)];
        }

        public bool Matches(string name) =>
            name.Contains(_text, StringComparison.OrdinalIgnoreCase) || (_chunks.Length >= 2 && CamelHumps(name));

        // Each chunk of the query must start the next hump of the name, from some hump on.
        private bool CamelHumps(string name)
        {
            for (var start = NextHump(name, -1); start >= 0; start = NextHump(name, start))
            {
                var position = start;
                var matched = 0;
                while (matched < _chunks.Length && position >= 0)
                {
                    var (chunkStart, chunkLength) = _chunks[matched];
                    if (position + chunkLength > name.Length
                        || string.Compare(name, position, _text, chunkStart, chunkLength, StringComparison.OrdinalIgnoreCase) != 0
                        || HumpEnd(name, position) - position < chunkLength)
                    {
                        break;
                    }

                    matched++;
                    position = NextHump(name, position);
                }

                if (matched == _chunks.Length)
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<(int Start, int Length)> Humps(string text)
        {
            for (var start = NextHump(text, -1); start >= 0;)
            {
                var next = NextHump(text, start);
                yield return (start, HumpEnd(text, start) - start);
                start = next;
            }
        }

        // Humps start at the first letter or digit, at each capital letter and after each underscore.
        private static int NextHump(string text, int current)
        {
            for (var i = current + 1; i < text.Length; i++)
            {
                if (text[i] == '_')
                {
                    continue;
                }

                if (current < 0 || char.IsUpper(text[i]) || text[i - 1] == '_')
                {
                    return i;
                }
            }

            return -1;
        }

        private static int HumpEnd(string text, int start)
        {
            var end = start + 1;
            while (end < text.Length && text[end] != '_' && !char.IsUpper(text[end]))
            {
                end++;
            }

            return end;
        }
    }
}

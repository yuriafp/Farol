namespace Farol.Engine.Markup;

public enum MarkupTarget
{
    /// <summary>A fully qualified type name, e.g. Inherits="App.Default" or x:Class="App.MainWindow".</summary>
    Type,

    /// <summary>A member of a container type, e.g. OnClick="Save_Click" on a page whose code-behind is the container.</summary>
    Member,
}

public enum MemberFilter
{
    Any,
    Method,

    /// <summary>A method shaped like an event handler: (sender, e) where e derives from System.EventArgs.</summary>
    EventHandler,
    FieldOrProperty,
}

/// <summary>What a markup file says, before it is resolved against a compilation.</summary>
public sealed record RawMarkupReference(
    int Line,
    int Column,
    string Snippet,
    MarkupTarget Target,
    string Name,
    string? ContainerType,
    MemberFilter Filter,
    string Detail);

/// <summary>A markup reference resolved to a symbol.</summary>
public sealed record MarkupReference(string FilePath, int Line, int Column, string Snippet, string Project, string Detail);

/// <summary>Maps character offsets to 1-based lines and columns and extracts line snippets.</summary>
internal sealed class MarkupText
{
    private const int MaxSnippetLength = 160;

    private readonly string _text;
    private readonly List<int> _lineStarts = [0];

    public MarkupText(string text)
    {
        _text = text;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                _lineStarts.Add(i + 1);
            }
        }
    }

    public (int Line, int Column) Position(int offset)
    {
        var index = _lineStarts.BinarySearch(offset);
        var line = index >= 0 ? index : ~index - 1;
        return (line + 1, offset - _lineStarts[line] + 1);
    }

    public string Snippet(int line)
    {
        if (line < 1 || line > _lineStarts.Count)
        {
            return string.Empty;
        }

        var start = _lineStarts[line - 1];
        var end = line < _lineStarts.Count ? _lineStarts[line] : _text.Length;
        var snippet = _text[start..end].Trim();
        return snippet.Length > MaxSnippetLength ? string.Concat(snippet.AsSpan(0, MaxSnippetLength), "…") : snippet;
    }

    public RawMarkupReference Reference(int offset, MarkupTarget target, string name, string? container, MemberFilter filter, string detail)
    {
        var (line, column) = Position(offset);
        return new RawMarkupReference(line, column, Snippet(line), target, name, container, filter, detail);
    }

    public RawMarkupReference Reference(int line, int column, MarkupTarget target, string name, string? container, MemberFilter filter, string detail) =>
        new(line, column, Snippet(line), target, name, container, filter, detail);
}

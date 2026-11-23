using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Farol.Engine.Loading;

/// <summary>
/// MSBuild property expansion and condition evaluation for a heuristic load: <c>$(Name)</c> (an undefined property is
/// empty, or the environment variable of that name, as in MSBuild), and conditions made of quoted strings, ==, !=, &lt;,
/// &gt;, &lt;=, &gt;=, And, Or, !, parentheses, Exists() and HasTrailingSlash(). A condition it cannot read (a property
/// function, say) is taken as true.
/// </summary>
internal sealed partial class ConditionEvaluator(Dictionary<string, string> properties, string directory)
{
    public bool Applies(XElement element) =>
        (string?)element.Attribute("Condition") is not { } condition || Evaluate(condition);

    public string Expand(string value) =>
        value.Contains("$(", StringComparison.Ordinal)
            ? PropertyReference().Replace(value, m => properties.TryGetValue(m.Groups[1].Value, out var v) ? v : Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? string.Empty)
            : value;

    public bool Evaluate(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        try
        {
            var parser = new Parser(Tokenize(condition), this);
            var result = parser.Or();
            return parser.AtEnd ? result : true;
        }
        catch (FormatException)
        {
            return true;
        }
    }

    [GeneratedRegex(@"\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)")]
    private static partial Regex PropertyReference();

    private enum Kind
    {
        String,
        Word,
        Operator,
        Open,
        Close,
        Comma,
    }

    private readonly record struct Token(Kind Kind, string Text);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '\'')
            {
                var end = text.IndexOf('\'', i + 1);
                if (end < 0)
                {
                    throw new FormatException("Unterminated string.");
                }

                tokens.Add(new Token(Kind.String, text[(i + 1)..end]));
                i = end + 1;
            }
            else if (c == '(')
            {
                tokens.Add(new Token(Kind.Open, "("));
                i++;
            }
            else if (c == ')')
            {
                tokens.Add(new Token(Kind.Close, ")"));
                i++;
            }
            else if (c == ',')
            {
                tokens.Add(new Token(Kind.Comma, ","));
                i++;
            }
            else if (c is '=' or '!' or '<' or '>')
            {
                var two = i + 1 < text.Length && text[i + 1] == '=';
                tokens.Add(new Token(Kind.Operator, two ? text.Substring(i, 2) : c.ToString()));
                i += two ? 2 : 1;
            }
            else if (c == '$' && i + 1 < text.Length && text[i + 1] == '(')
            {
                // An unquoted $(Name): a string. A property function $([…]) is not read.
                var end = text.IndexOf(')', i);
                if (end < 0 || text[i + 2] == '[')
                {
                    throw new FormatException("Property function.");
                }

                tokens.Add(new Token(Kind.String, text[i..(end + 1)]));
                i = end + 1;
            }
            else if (char.IsLetterOrDigit(c) || c is '_' or '.' or '-')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.' or '-'))
                {
                    i++;
                }

                tokens.Add(new Token(Kind.Word, text[start..i]));
            }
            else
            {
                throw new FormatException($"Unexpected '{c}'.");
            }
        }

        return tokens;
    }

    // Recursive descent; "evaluate: false" parses the side And/Or skip, as MSBuild short-circuits them (a guard such
    // as '$(X)' != '' And '$(X)' >= '11.0' must not compare an empty value).
    private sealed class Parser(List<Token> tokens, ConditionEvaluator owner)
    {
        private int _position;

        public bool AtEnd => _position == tokens.Count;

        public bool Or(bool evaluate = true)
        {
            var value = And(evaluate);
            while (IsWord("Or"))
            {
                _position++;
                var right = And(evaluate && !value);
                value = value || right;
            }

            return value;
        }

        private bool And(bool evaluate)
        {
            var value = Unary(evaluate);
            while (IsWord("And"))
            {
                _position++;
                var right = Unary(evaluate && value);
                value = value && right;
            }

            return value;
        }

        private bool Unary(bool evaluate)
        {
            if (Peek() is { Kind: Kind.Operator, Text: "!" })
            {
                _position++;
                return !Unary(evaluate);
            }

            return Primary(evaluate);
        }

        private bool Primary(bool evaluate)
        {
            var token = Next();
            if (token.Kind == Kind.Open)
            {
                var value = Or(evaluate);
                Expect(Kind.Close);
                return value;
            }

            string left;
            if (token.Kind == Kind.Word && Peek() is { Kind: Kind.Open })
            {
                _position++;
                var argument = Next().Text;
                Expect(Kind.Close);
                var result = evaluate && Function(token.Text, Operand(argument));
                if (Peek() is not { Kind: Kind.Operator, Text: not "!" })
                {
                    return result;
                }

                left = result ? "true" : "false";
            }
            else
            {
                left = token.Kind is Kind.String or Kind.Word ? (evaluate ? Operand(token.Text) : string.Empty) : throw new FormatException("Operand expected.");
            }

            if (Peek() is { Kind: Kind.Operator, Text: not "!" } op)
            {
                _position++;
                var right = Next();
                if (right.Kind is not (Kind.String or Kind.Word))
                {
                    throw new FormatException("Operand expected.");
                }

                return evaluate && Compare(left, op.Text, Operand(right.Text));
            }

            if (!evaluate)
            {
                return false;
            }

            return left.Trim().ToUpperInvariant() switch
            {
                "TRUE" or "ON" or "YES" => true,
                "FALSE" or "OFF" or "NO" => false,
                _ => throw new FormatException($"Not a boolean: {left}."),
            };
        }

        // A value with its properties expanded; what is left of a property function ($([…]), $(X.Method())) is not read.
        private string Operand(string text)
        {
            var value = owner.Expand(text);
            return value.Contains("$(", StringComparison.Ordinal) ? throw new FormatException("Property function.") : value;
        }

        private bool Function(string name, string argument) => name.ToUpperInvariant() switch
        {
            "EXISTS" => argument.Trim().Length > 0 && Exists(argument.Trim()),
            "HASTRAILINGSLASH" => argument.EndsWith('/') || argument.EndsWith('\\'),
            _ => throw new FormatException($"Function {name}."),
        };

        private bool Exists(string path)
        {
            var full = ClassicProjectFile.Full(owner._directory, path);
            return PathCase.File(full) is not null || PathCase.Directory(full) is not null;
        }

        private static bool Compare(string left, string op, string right)
        {
            left = left.Trim();
            right = right.Trim();
            switch (op)
            {
                case "==":
                    return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
                case "!=":
                    return !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }

            var l = left.TrimStart('v', 'V');
            var r = right.TrimStart('v', 'V');
            if (double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out var ln) && double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var rn))
            {
                return Order(ln.CompareTo(rn), op);
            }

            // Versions such as 4.7.2: compare as System.Version.
            if (Version.TryParse(l.Contains('.', StringComparison.Ordinal) ? l : l + ".0", out var lv) && Version.TryParse(r.Contains('.', StringComparison.Ordinal) ? r : r + ".0", out var rv))
            {
                return Order(lv.CompareTo(rv), op);
            }

            throw new FormatException("Not a number.");
        }

        private static bool Order(int comparison, string op) => op switch
        {
            "<" => comparison < 0,
            ">" => comparison > 0,
            "<=" => comparison <= 0,
            ">=" => comparison >= 0,
            _ => throw new FormatException($"Operator {op}."),
        };

        private bool IsWord(string word) => Peek() is { Kind: Kind.Word } t && string.Equals(t.Text, word, StringComparison.OrdinalIgnoreCase);

        private Token? Peek() => _position < tokens.Count ? tokens[_position] : null;

        private Token Next() => _position < tokens.Count ? tokens[_position++] : throw new FormatException("Unexpected end.");

        private void Expect(Kind kind)
        {
            if (Next().Kind != kind)
            {
                throw new FormatException($"{kind} expected.");
            }
        }
    }

    private readonly string _directory = directory;
}

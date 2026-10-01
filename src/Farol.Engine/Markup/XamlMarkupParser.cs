using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Farol.Engine.Markup;

/// <summary>
/// Reads XAML (WPF, and the same syntax in UWP/WinUI/MAUI): x:Class names the code-behind, clr-namespace
/// elements and {x:Type}/{x:Static} reference types, x:Name declares fields, and attribute values that name a
/// code-behind method with an event-handler signature are event wiring.
/// </summary>
internal static partial class XamlMarkupParser
{
    private const string XamlLanguage = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XName ClassAttribute = XName.Get("Class", XamlLanguage);
    private static readonly XName NameAttribute = XName.Get("Name", XamlLanguage);

    public static List<RawMarkupReference> Parse(string content)
    {
        XElement root;
        try
        {
            root = XDocument.Parse(content, LoadOptions.SetLineInfo).Root!;
        }
        catch (XmlException)
        {
            return [];
        }

        var text = new MarkupText(content);
        var results = new List<RawMarkupReference>();
        var classAttribute = root.Attribute(ClassAttribute);
        var container = classAttribute?.Value.Trim() is { Length: > 0 } name ? name : null;
        if (container is not null)
        {
            results.Add(Reference(text, classAttribute!, MarkupTarget.Type, container, null, MemberFilter.Any, "code-behind class"));
        }

        foreach (var element in root.DescendantsAndSelf())
        {
            if (ClrNamespace(element.Name.Namespace) is { } elementNamespace)
            {
                // Property elements look like "Type.Property": the type is the part before the dot.
                var typeName = element.Name.LocalName.Split('.')[0];
                results.Add(Reference(text, element, MarkupTarget.Type, $"{elementNamespace}.{typeName}", null, MemberFilter.Any, "element type"));
            }

            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name != ClassAttribute))
            {
                var value = attribute.Value.Trim();
                if (value.StartsWith('{'))
                {
                    AddMarkupExtensions(text, element, attribute, value, results);
                }
                else if (container is null || !WebFormsMarkupParser.IsIdentifier(value))
                {
                    continue;
                }
                else if (attribute.Name == NameAttribute || attribute.Name == XName.Get("Name"))
                {
                    results.Add(Reference(text, attribute, MarkupTarget.Member, value, container, MemberFilter.FieldOrProperty, "named element"));
                }
                else if (attribute.Name.Namespace == XNamespace.None)
                {
                    // Handler="…" on an EventSetter is explicit; any other plain attribute is checked against the handler signature.
                    var filter = attribute.Name.LocalName == "Handler" && element.Name.LocalName == "EventSetter" ? MemberFilter.Method : MemberFilter.EventHandler;
                    results.Add(Reference(text, attribute, MarkupTarget.Member, value, container, filter, "event handler"));
                }
            }
        }

        return results;
    }

    private static void AddMarkupExtensions(MarkupText text, XElement element, XAttribute attribute, string value, List<RawMarkupReference> results)
    {
        foreach (Match extension in TypeExtension().Matches(value))
        {
            if (ClrNamespace(element.GetNamespaceOfPrefix(extension.Groups["prefix"].Value)) is not { } ns)
            {
                continue;
            }

            var name = extension.Groups["name"].Value;
            if (extension.Groups["kind"].Value == "Type")
            {
                results.Add(Reference(text, attribute, MarkupTarget.Type, $"{ns}.{name}", null, MemberFilter.Any, "type"));
            }
            else if (name.LastIndexOf('.') is > 0 and var dot)
            {
                var type = $"{ns}.{name[..dot]}";
                results.Add(Reference(text, attribute, MarkupTarget.Type, type, null, MemberFilter.Any, "type"));
                results.Add(Reference(text, attribute, MarkupTarget.Member, name[(dot + 1)..], type, MemberFilter.Any, "static member"));
            }
        }
    }

    private static RawMarkupReference Reference(MarkupText text, IXmlLineInfo position, MarkupTarget target, string name, string? container, MemberFilter filter, string detail) =>
        text.Reference(position.LineNumber, position.LinePosition, target, name, container, filter, detail);

    private static string? ClrNamespace(XNamespace? xmlNamespace)
    {
        var name = xmlNamespace?.NamespaceName ?? string.Empty;
        if (name.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            return name["clr-namespace:".Length..].Split(';')[0].Trim();
        }

        return name.StartsWith("using:", StringComparison.Ordinal) ? name["using:".Length..].Trim() : null;
    }

    // {x:Type local:Foo}, {x:Static local:Foo.Bar}
    [GeneratedRegex(@"\{\s*x:(?<kind>Type|Static)\s+(?:(?:TypeName|Member)\s*=\s*)?(?<prefix>\w+):(?<name>[\w.]+)")]
    private static partial Regex TypeExtension();
}

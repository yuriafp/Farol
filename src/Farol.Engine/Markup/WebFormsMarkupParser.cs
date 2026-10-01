using System.Text.RegularExpressions;

namespace Farol.Engine.Markup;

/// <summary>
/// Reads WebForms-style markup (.aspx, .ascx, .master, .asax, .ashx, .asmx, .svc). It is not XML, so it is
/// scanned: directives name the code-behind or service class; server controls wire event handlers, control
/// fields and model-binding methods; &lt;% %&gt; blocks call members of the page.
/// </summary>
internal static partial class WebFormsMarkupParser
{
    private static readonly string[] DataMethodAttributes = ["SelectMethod", "InsertMethod", "UpdateMethod", "DeleteMethod"];

    // Wired by name, never referenced: pages/controls with AutoEventWireup, and Global.asax (always).
    private static readonly string[] PageEvents =
    [
        "Page_PreInit", "Page_Init", "Page_InitComplete", "Page_PreLoad", "Page_Load", "Page_LoadComplete", "Page_PreRender",
        "Page_PreRenderComplete", "Page_SaveStateComplete", "Page_Unload", "Page_Error", "Page_DataBind",
    ];

    private static readonly string[] ApplicationEvents =
    [
        "Application_Start", "Application_End", "Application_Error", "Application_BeginRequest", "Application_EndRequest",
        "Application_AuthenticateRequest", "Application_PostAuthenticateRequest", "Application_AuthorizeRequest",
        "Application_AcquireRequestState", "Application_PreRequestHandlerExecute", "Application_PostRequestHandlerExecute",
        "Application_PreSendRequestHeaders", "Session_Start", "Session_End",
    ];

    public static List<RawMarkupReference> Parse(string content)
    {
        var text = new MarkupText(content);
        var results = new List<RawMarkupReference>();
        string? container = null;

        foreach (Match directive in Directive().Matches(content))
        {
            var directiveAttributes = Attribute().Matches(directive.Groups["attributes"].Value).ToList();
            var inherits = directiveAttributes.FirstOrDefault(a => a.Groups["name"].Value.Equals("Inherits", StringComparison.OrdinalIgnoreCase))?.Groups["value"].Value.Trim();
            var autoWired = AutoWiredEvents(directive.Groups["kind"].Value, directiveAttributes);
            if (inherits is { Length: > 0 })
            {
                foreach (var eventMethod in autoWired)
                {
                    results.Add(text.Reference(directive.Index, MarkupTarget.Member, eventMethod, inherits, MemberFilter.Method, "auto-wired event"));
                }
            }

            foreach (var attribute in directiveAttributes)
            {
                var detail = attribute.Groups["name"].Value.ToUpperInvariant() switch
                {
                    "INHERITS" => "code-behind class",
                    "CLASS" or "SERVICE" => "service class",
                    "FACTORY" => "service host factory",
                    _ => null,
                };
                var value = attribute.Groups["value"];
                if (detail is null || value.Length == 0)
                {
                    continue;
                }

                var offset = directive.Groups["attributes"].Index + value.Index;
                results.Add(text.Reference(offset, MarkupTarget.Type, value.Value.Trim(), null, MemberFilter.Any, detail));
                if (detail == "code-behind class")
                {
                    container ??= value.Value.Trim();
                }
            }
        }

        foreach (Match tag in ServerTag().Matches(content))
        {
            var attributesGroup = tag.Groups["attributes"];
            var attributes = Attribute().Matches(attributesGroup.Value).ToList();
            if (!attributes.Any(a => a.Groups["name"].Value.Equals("runat", StringComparison.OrdinalIgnoreCase)
                                     && a.Groups["value"].Value.Equals("server", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var dataSourceType = attributes.FirstOrDefault(a => a.Groups["name"].Value.Equals("TypeName", StringComparison.OrdinalIgnoreCase))?.Groups["value"].Value;
            foreach (var attribute in attributes)
            {
                var name = attribute.Groups["name"].Value;
                var value = attribute.Groups["value"].Value.Trim();
                var offset = attributesGroup.Index + attribute.Groups["value"].Index;
                if (name.Equals("TypeName", StringComparison.OrdinalIgnoreCase) || name.Equals("ItemType", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Length > 0)
                    {
                        results.Add(text.Reference(offset, MarkupTarget.Type, value, null, MemberFilter.Any, name.Equals("ItemType", StringComparison.OrdinalIgnoreCase) ? "item type" : "data source type"));
                    }
                }
                else if (!IsIdentifier(value))
                {
                    continue;
                }
                else if (name.Equals("ID", StringComparison.OrdinalIgnoreCase) && container is not null)
                {
                    results.Add(text.Reference(offset, MarkupTarget.Member, value, container, MemberFilter.FieldOrProperty, "control"));
                }
                else if (DataMethodAttributes.Contains(name, StringComparer.OrdinalIgnoreCase) && (dataSourceType ?? container) is { } owner)
                {
                    results.Add(text.Reference(offset, MarkupTarget.Member, value, owner, MemberFilter.Method, "data method"));
                }
                else if (name.Length > 2 && name.StartsWith("On", StringComparison.Ordinal) && char.IsUpper(name[2]) && container is not null)
                {
                    results.Add(text.Reference(offset, MarkupTarget.Member, value, container, MemberFilter.Method, "event handler"));
                }
            }
        }

        if (container is not null)
        {
            foreach (Match block in CodeBlock().Matches(content))
            {
                var code = block.Groups["code"];
                var withoutStrings = StringLiteral().Replace(code.Value, m => new string(' ', m.Length));
                foreach (Match identifier in Identifier().Matches(withoutStrings))
                {
                    results.Add(text.Reference(code.Index + identifier.Index, MarkupTarget.Member, identifier.Value, container, MemberFilter.Any, "expression"));
                }
            }
        }

        return results;
    }

    internal static bool IsIdentifier(string value) => Identifier().Match(value) is { Success: true } match && match.Length == value.Length;

    private static string[] AutoWiredEvents(string directiveKind, List<Match> attributes)
    {
        if (directiveKind.Equals("Application", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationEvents;
        }

        if (!directiveKind.Equals("Page", StringComparison.OrdinalIgnoreCase)
            && !directiveKind.Equals("Control", StringComparison.OrdinalIgnoreCase)
            && !directiveKind.Equals("Master", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var autoEventWireup = attributes.FirstOrDefault(a => a.Groups["name"].Value.Equals("AutoEventWireup", StringComparison.OrdinalIgnoreCase));
        return autoEventWireup?.Groups["value"].Value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) == true ? [] : PageEvents;
    }

    [GeneratedRegex(@"<%@\s*(?<kind>\w+)(?<attributes>(?:[^%]|%(?!>))*)%>")]
    private static partial Regex Directive();

    [GeneratedRegex(@"<(?<tag>[A-Za-z][\w:.-]*)(?<attributes>[^<>]*)>")]
    private static partial Regex ServerTag();

    [GeneratedRegex("""(?<name>[\w:.-]+)\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)')""")]
    private static partial Regex Attribute();

    // <% %>, <%= %>, <%: %>, <%# %>; not directives (<%@), expression builders (<%$) or comments (<%--).
    [GeneratedRegex(@"<%(?![@$]|--)[=:#]?(?<code>(?:[^%]|%(?!>))*)%>")]
    private static partial Regex CodeBlock();

    // Identifiers not accessed through another object ("x.Name"), except through this./Me.
    [GeneratedRegex(@"(?:(?<=\bthis\.)|(?<=\bMe\.)|(?<![\w.]))[A-Za-z_]\w*")]
    private static partial Regex Identifier();

    [GeneratedRegex("""
        "(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*'
        """)]
    private static partial Regex StringLiteral();
}

using Farol.Engine.Markup;
using Farol.Testing;
using Xunit;

namespace Farol.Engine.Tests;

public sealed class MarkupParserTests
{
    [Fact]
    public void WebForms_page_references_code_behind_controls_handlers_and_expressions()
    {
        var references = WebFormsMarkupParser.Parse(File.ReadAllText(Path.Combine(TestPaths.LegacyDirectory, "Legacy.Web", "Default.aspx")));

        Assert.Contains(references, r => r is { Target: MarkupTarget.Type, Name: "Legacy.Web._Default", Detail: "code-behind class", Line: 1 });
        Assert.Contains(references, r => r is { Name: "btnCalculate_Click", Detail: "event handler", Line: 12, ContainerType: "Legacy.Web._Default" });
        Assert.Contains(references, r => r is { Name: "lblTotal", Detail: "control", Line: 11 });
        Assert.Contains(references, r => r is { Name: "Greeting", Detail: "expression", Line: 9 });
        Assert.Contains(references, r => r is { Name: "Page_Load", Detail: "auto-wired event", Line: 1 });
    }

    [Fact]
    public void String_literals_in_code_blocks_are_not_identifiers()
    {
        var references = WebFormsMarkupParser.Parse("<%@ Page Inherits=\"App.Home\" %>\n<p><%# Eval(\"Title\") %></p>");

        Assert.DoesNotContain(references, r => r.Name == "Title");
        Assert.Contains(references, r => r is { Name: "Eval", Detail: "expression", Line: 2 });
    }

    [Fact]
    public void AutoEventWireup_false_turns_off_convention_wiring()
    {
        var references = WebFormsMarkupParser.Parse("<%@ Page Inherits=\"App.Home\" AutoEventWireup=\"false\" %>");

        Assert.DoesNotContain(references, r => r.Detail == "auto-wired event");
    }

    [Fact]
    public void Global_asax_wires_application_and_session_events()
    {
        var references = WebFormsMarkupParser.Parse("<%@ Application Codebehind=\"Global.asax.cs\" Inherits=\"App.Global\" %>");

        Assert.Contains(references, r => r is { Name: "Application_Start", ContainerType: "App.Global", Detail: "auto-wired event" });
        Assert.Contains(references, r => r is { Name: "Session_Start", Detail: "auto-wired event" });
    }

    [Fact]
    public void Service_and_handler_directives_reference_their_classes()
    {
        var asmx = WebFormsMarkupParser.Parse("<%@ WebService Language=\"C#\" CodeBehind=\"S.asmx.cs\" Class=\"App.OrdersService\" %>");
        var svc = WebFormsMarkupParser.Parse("<%@ ServiceHost Service=\"App.OrderService\" Factory=\"App.HostFactory\" %>");

        Assert.Equal("App.OrdersService", Assert.Single(asmx).Name);
        Assert.Equal(["App.OrderService", "App.HostFactory"], svc.Select(r => r.Name));
        Assert.All(svc, r => Assert.Equal(MarkupTarget.Type, r.Target));
    }

    [Fact]
    public void Object_data_source_methods_resolve_against_its_type_name()
    {
        var references = WebFormsMarkupParser.Parse(
            "<%@ Page Inherits=\"App.Home\" %>\n<asp:ObjectDataSource ID=\"Orders\" runat=\"server\" TypeName=\"App.OrderRepository\" SelectMethod=\"GetAll\" />");

        Assert.Contains(references, r => r is { Target: MarkupTarget.Type, Name: "App.OrderRepository", Detail: "data source type" });
        Assert.Contains(references, r => r is { Name: "GetAll", ContainerType: "App.OrderRepository", Detail: "data method" });
    }

    [Fact]
    public void Xaml_window_references_its_class_types_handlers_and_named_elements()
    {
        var references = XamlMarkupParser.Parse(File.ReadAllText(Path.Combine(TestPaths.LegacyDirectory, "Legacy.Wpf", "MainWindow.xaml")));

        Assert.Contains(references, r => r is { Target: MarkupTarget.Type, Name: "Legacy.Wpf.MainWindow", Detail: "code-behind class", Line: 1 });
        Assert.Contains(references, r => r is { Target: MarkupTarget.Type, Name: "Legacy.Wpf.PriceConverter", Detail: "element type", Line: 7 });
        Assert.Contains(references, r => r is { Name: "OnCalculateClick", Filter: MemberFilter.EventHandler, Line: 10 });
        Assert.Contains(references, r => r is { Name: "TotalText", Detail: "named element", Line: 11 });
    }

    [Fact]
    public void Xaml_markup_extensions_reference_types_and_static_members()
    {
        const string xaml = """
            <UserControl x:Class="App.Panel"
                         xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:local="clr-namespace:App.Ui;assembly=App">
                <Style TargetType="{x:Type local:Badge}" />
                <TextBlock Text="{x:Static local:Labels.Title}" />
            </UserControl>
            """;

        var references = XamlMarkupParser.Parse(xaml);

        Assert.Contains(references, r => r is { Target: MarkupTarget.Type, Name: "App.Ui.Badge", Line: 5 });
        Assert.Contains(references, r => r is { Target: MarkupTarget.Type, Name: "App.Ui.Labels", Line: 6 });
        Assert.Contains(references, r => r is { Target: MarkupTarget.Member, Name: "Title", ContainerType: "App.Ui.Labels", Detail: "static member" });
    }

    [Fact]
    public void Invalid_xaml_yields_no_references() =>
        Assert.Empty(XamlMarkupParser.Parse("<Window x:Class=\"App.Broken\""));
}

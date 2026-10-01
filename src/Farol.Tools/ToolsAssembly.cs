using System.Reflection;

namespace Farol.Tools;

/// <summary>Anchor for registering every Farol tool: <c>WithToolsFromAssembly(ToolsAssembly.Assembly)</c>.</summary>
public static class ToolsAssembly
{
    public static Assembly Assembly { get; } = typeof(ToolsAssembly).Assembly;
}

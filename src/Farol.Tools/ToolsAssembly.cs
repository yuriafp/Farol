using System.Reflection;

namespace Farol.Tools;

/// <summary>
/// Anchor for registering every Farol prompt: <c>WithPromptsFromAssembly(ToolsAssembly.Assembly)</c>. Tools go through
/// <see cref="ToolsMcpServerBuilderExtensions.WithFarolTools"/>.
/// </summary>
public static class ToolsAssembly
{
    public static Assembly Assembly { get; } = typeof(ToolsAssembly).Assembly;
}

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Farol.Tools;

/// <summary>Registers every Farol tool, for the host and the tests alike.</summary>
public static class ToolsMcpServerBuilderExtensions
{
    /// <summary>
    /// Leaves the progress reporter, which the SDK binds from the request, out of every tool's schema by its type. The SDK
    /// marks it to be left out already, but AIFunctionFactory looks that mark up by <c>ParameterInfo</c> instance, and
    /// <c>MethodInfo.GetParameters()</c> hands different instances to threads that call it for the first time together:
    /// servers built in parallel in one process, as the tests build them, now and then listed <c>progress</c> as an
    /// argument. Tools take services through their constructor, so no other parameter is bound.
    /// </summary>
    private static readonly AIJsonSchemaCreateOptions SchemaOptions = new()
    {
        IncludeParameter = parameter => parameter.ParameterType != typeof(IProgress<ProgressNotificationValue>),
    };

    /// <summary>
    /// Adds the <see cref="McpServerToolAttribute"/> methods of this assembly's <see cref="McpServerToolTypeAttribute"/>
    /// types, as the SDK's <c>WithToolsFromAssembly</c> does but with <see cref="SchemaOptions"/>. Each call creates the
    /// tool's type with the services its constructor takes.
    /// </summary>
    public static IMcpServerBuilder WithFarolTools(this IMcpServerBuilder builder)
    {
        foreach (var type in ToolsAssembly.Assembly.GetTypes().Where(t => t.IsDefined(typeof(McpServerToolTypeAttribute), inherit: false)))
        {
            foreach (var method in type.GetMethods().Where(m => m.IsDefined(typeof(McpServerToolAttribute), inherit: false)))
            {
                builder.Services.AddSingleton(services => McpServerTool.Create(
                    method,
                    request => ActivatorUtilities.CreateInstance(request.Services!, type),
                    new McpServerToolCreateOptions { Services = services, SchemaCreateOptions = SchemaOptions }));
            }
        }

        return builder;
    }
}

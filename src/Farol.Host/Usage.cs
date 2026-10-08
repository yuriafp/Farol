namespace Farol.Host;

/// <summary>What <c>farol --help</c> prints, for whoever runs Farol in a terminal to check the install.</summary>
internal static class Usage
{
    private static readonly HashSet<string> Switches = new(StringComparer.OrdinalIgnoreCase) { "--help", "-h", "-?", "/?" };

    public static bool IsRequested(IEnumerable<string> args) => args.Any(Switches.Contains);

    // ASCII only: Windows consoles may still use a legacy code page.
    public static string Text { get; } = $$"""
        Farol {{HostConfiguration.Version}}: compiler-accurate C# and VB code intelligence for AI agents, as an MCP server.

        Usage: farol [options]

        An MCP client starts Farol and talks to it over stdin and stdout: started in a terminal, Farol
        waits for a client. Add it to the client's MCP configuration (such as .mcp.json) in one of
        these forms:

          "farol": { "command": "farol" }
          "farol": { "command": "dotnet", "args": ["dnx", "Farol.Mcp@{{HostConfiguration.Version}}", "--yes"] }

        The first runs the .NET tool: dotnet tool install --global Farol.Mcp --version {{HostConfiguration.Version}}
        The second runs the package from NuGet without installing it, with Farol's options after "--".
        Farol discovers the solution in the client's working directory.

        Options:
          --root <dir>                     The directory Farol trusts and discovers the solution in.
                                           Default: the working directory.
          --workspace <path>               The solution or project used when a tool call names none.
                                           Default: the one discovered in the root.
          --autoload false                 Don't start loading the solution at startup.
          --read-only                      Refuse writing files, building and running tests.
          --offline                        Never touch the network.
          --Farol:TrustedPaths:0 <dir>     More directories whose solutions may be loaded and whose files
                                           may be read or written (:1, :2 and so on for more).
          --Farol:BuildTimeoutMinutes <n>  Stop a longer build with its whole process tree. Default: 15.
          --Farol:TestTimeoutMinutes <n>   The same, per test project run. Default: 20.
          -h, --help                       Show this help.

        Settings can also come from environment variables, such as Farol__ReadOnly=true, and from
        appsettings.json next to the executable. Documentation: https://github.com/yuriafp/Farol
        """;
}

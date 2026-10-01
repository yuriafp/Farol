using Farol.Core.Paths;
using Xunit;

namespace Farol.Engine.Tests;

public sealed class DisplayPathTests
{
    [Fact]
    public void Paths_under_the_root_inside_a_message_become_relative()
    {
        var root = Path.Combine(Path.GetTempPath(), "Farol Root");
        var message = $"Msbuild failed when processing the file '{Path.Combine(root, "src", "App", "App.csproj")}' with message: see {root}/docs/x.md.";

        var text = DisplayPath.InText(root, message);

        Assert.Equal("Msbuild failed when processing the file 'src/App/App.csproj' with message: see docs/x.md.", text);
    }

    [Fact]
    public void Paths_outside_the_root_and_other_text_are_kept()
    {
        var root = Path.Combine(Path.GetTempPath(), "Farol");
        var other = Path.Combine(Path.GetTempPath(), "FarolOther", "App.csproj");

        Assert.Equal($"file '{other}' failed", DisplayPath.InText(root, $"file '{other}' failed"));
        Assert.Equal("no paths here", DisplayPath.InText(root, "no paths here"));
    }
}

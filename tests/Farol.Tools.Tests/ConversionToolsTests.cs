using Farol.Testing;
using Xunit;

namespace Farol.Tools.Tests;

/// <summary>Spec 002, AC-66 – AC-67: dotnet_convert_project on the legacy fixture's classic projects.</summary>
public sealed class ConversionToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Has(string text, string expected) => Assert.True(text.Contains(expected, StringComparison.Ordinal), $"Missing: {expected}\n{text}");

    [Fact]
    public async Task AC66_sdk_style_preview_keeps_the_target_framework_and_writes_nothing()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");
        var projectFile = server.Copy.PathOf("Legacy.Core", "Legacy.Core.csproj");
        var before = await File.ReadAllTextAsync(projectFile, Ct);

        var text = await server.CallAsync("dotnet_convert_project", new() { ["project"] = "Legacy.Core", ["kind"] = "sdk_style" });

        Has(text, "preview of convert Legacy.Core to an SDK-style project");
        Has(text, "+<Project Sdk=\"Microsoft.NET.Sdk\">");
        Has(text, "+    <TargetFramework>net48</TargetFramework>");
        Has(text, "+    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>");
        Has(text, "     <Reference Include=\"System.Configuration\" />");
        Assert.DoesNotContain("-    <Reference Include=\"System.Configuration\" />", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<Compile Include=", text.Split('\n').Where(l => l.StartsWith('+')).Aggregate(string.Empty, (a, l) => a + l), StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(projectFile, Ct));
    }

    [Fact]
    public async Task AC66_sdk_style_apply_reloads_the_project_with_the_same_files_and_references()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");

        var text = await server.CallAsync("dotnet_convert_project", new() { ["project"] = "Legacy.Core", ["kind"] = "sdk_style", ["apply"] = true });

        Has(text, "applied convert Legacy.Core to an SDK-style project");
        Has(text, "after reloading: same source files, references and packages, and no new error");
        Has(await File.ReadAllTextAsync(server.Copy.PathOf("Legacy.Core", "Legacy.Core.csproj"), Ct), "<Project Sdk=\"Microsoft.NET.Sdk\">");
        var overview = await server.CallAsync("dotnet_overview", []);
        Has(overview, "Legacy.Core");
    }

    [Fact]
    public async Task AC66_a_visual_basic_project_keeps_its_imports_and_options()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");

        var text = await server.CallAsync("dotnet_convert_project", new() { ["project"] = "Legacy.VbLib", ["kind"] = "sdk_style", ["apply"] = true });

        Has(text, "+    <DisableImplicitNamespaceImports>true</DisableImplicitNamespaceImports>");
        Has(text, "after reloading: same source files, references and packages, and no new error");
    }

    [Fact]
    public async Task AC67_package_reference_replaces_packages_config_and_the_reload_resolves_the_same_packages()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");

        var preview = await server.CallAsync("dotnet_convert_project", new() { ["project"] = "Legacy.Tests", ["kind"] = "package_reference" });
        Has(preview, "+    <PackageReference Include=\"MSTest.TestAdapter\" Version=\"2.2.10\" />");
        Has(preview, "+    <PackageReference Include=\"Newtonsoft.Json\" Version=\"12.0.3\" />");
        Has(preview, "+++ /dev/null");
        Has(preview, "-  <package id=\"Newtonsoft.Json\" version=\"12.0.3\" targetFramework=\"net48\" />");
        Has(preview, "-  <Import Project=\"..\\packages\\MSTest.TestAdapter.2.2.10\\build\\net45\\MSTest.TestAdapter.props\"");

        var text = await server.CallAsync("dotnet_convert_project", new() { ["project"] = "Legacy.Tests", ["kind"] = "package_reference", ["apply"] = true });

        Has(text, "after reloading: same source files, references and packages, and no new error");
        Assert.False(File.Exists(server.Copy.PathOf("Legacy.Tests", "packages.config")));
    }

    [Fact]
    public async Task Conversion_of_a_web_application_project_is_refused_with_the_reason()
    {
        await using var server = await LegacyServer.StartAsync();
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");

        var (isError, text) = await server.Harness!.CallAsync("dotnet_convert_project", new() { ["project"] = "Legacy.Web", ["kind"] = "sdk_style" }, Ct);

        Assert.True(isError, text);
        Has(text, "web application");
        Has(text, "Nothing was written");
    }

    [Fact]
    public async Task Conversion_with_apply_is_refused_in_read_only_mode()
    {
        await using var server = await LegacyServer.StartAsync(o => o.ReadOnly = true);
        Assert.SkipUnless(server.Available, "Needs Windows with Visual Studio Build Tools to load the legacy fixture.");

        var preview = await server.CallAsync("dotnet_convert_project", new() { ["project"] = "Legacy.Core", ["kind"] = "sdk_style" });
        var (isError, text) = await server.Harness!.CallAsync("dotnet_convert_project", new() { ["project"] = "Legacy.Core", ["kind"] = "sdk_style", ["apply"] = true }, Ct);

        Has(preview, "preview of convert Legacy.Core");
        Assert.True(isError, text);
        Has(text, "Refused to write files");
    }
}

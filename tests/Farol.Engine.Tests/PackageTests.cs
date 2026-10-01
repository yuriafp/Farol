using Farol.Engine.Navigation;
using Farol.Engine.Packages;
using Microsoft.CodeAnalysis;
using NuGet.Frameworks;
using NuGet.Versioning;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>Packages as restore left them on disk in the modern fixture, and the public API of a restored package.</summary>
[Collection(ModernFixtureDefinition.Name)]
public sealed class ModernPackageTests(ModernWorkspaceFixture fixture)
{
    private NuGetContext Context => NuGetContext.Load(fixture.Root);

    [Fact]
    public void Transitive_packages_carry_the_chain_that_brings_them_in()
    {
        var api = Assert.Single(PackageInventory.Read(fixture.Snapshot, Context, "Modern.Api"));

        Assert.Equal(PackageStyle.PackageReference, api.Style);
        Assert.True(api.CentralPackageManagement);
        Assert.EndsWith("Directory.Packages.props", api.VersionsFile, StringComparison.Ordinal);
        var direct = Assert.Single(api.Packages, p => p.IsDirect);
        Assert.Equal(("Microsoft.AspNetCore.Authentication.JwtBearer", "8.0.0"), (direct.Id, direct.Version.ToNormalizedString()));
        var jwt = Assert.Single(api.Packages, p => p.Id == "System.IdentityModel.Tokens.Jwt");
        Assert.False(jwt.IsDirect);
        Assert.Equal(["Microsoft.AspNetCore.Authentication.JwtBearer", "Microsoft.IdentityModel.Protocols.OpenIdConnect"], jwt.Via);
        Assert.Equal(7, api.Packages.Count(p => !p.IsDirect));
    }

    [Fact]
    public void Packages_of_one_target_framework_list_it()
    {
        var core = Assert.Single(PackageInventory.Read(fixture.Snapshot, Context, "Modern.Core"));

        Assert.Equal(["net10.0", "net48"], core.Frameworks);
        var json = Assert.Single(core.Packages, p => p.IsDirect);
        Assert.Equal(("System.Text.Json", "8.0.4"), (json.Id, json.Version.ToNormalizedString()));
        Assert.Equal(["net48"], json.Frameworks);
        Assert.All(core.Packages.Where(p => !p.IsDirect), p => Assert.Equal(["System.Text.Json"], p.Via.Take(1)));
    }

    [Fact]
    public void The_restore_audit_is_read_without_depending_on_the_message_language()
    {
        var projects = PackageInventory.Read(fixture.Snapshot, Context, projectName: null);

        var findings = projects.SelectMany(p => p.RestoreAudit).ToList();
        Assert.Contains(findings, f => f.Id == "System.Text.Json" && f.Version == NuGetVersion.Parse("8.0.4") && f.Severity == "high"
            && f.AdvisoryUrl == new Uri("https://github.com/advisories/GHSA-8g4q-xg66-9fp4"));
        Assert.Contains(findings, f => f.Id == "System.IdentityModel.Tokens.Jwt" && f.Severity == "moderate");
    }

    [Fact]
    public void Package_api_picks_the_framework_folder_and_reads_signatures_with_docs()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = Context.FindInstalled("System.Text.Json", NuGetVersion.Parse("8.0.4"));
        Assert.NotNull(folder);

        var forNet48 = PackageApi.SelectAssemblies(folder, NuGetFramework.Parse("net48"));
        var newest = PackageApi.SelectAssemblies(folder, target: null);
        var api = PackageApi.Open(newest!);
        var serializer = Assert.Single(api.Find("JsonSerializer"));
        var serialize = api.Find("JsonSerializer.Serialize");
        var described = serialize.Select(s => PackageApi.Describe(s, withContainingType: true, ct)).ToList();

        Assert.Equal("lib/net462", forNet48!.Folder);
        Assert.Equal("lib/net8.0", newest!.Folder);
        Assert.Contains(NuGetFramework.Parse("netstandard2.0"), newest.Available);
        Assert.Equal("T:System.Text.Json.JsonSerializer", DocumentationCommentId.CreateDeclarationId(serializer));
        Assert.All(serialize, s => Assert.Equal("Serialize", s.Name));
        Assert.Contains(described, d => d.Signature.StartsWith("static string JsonSerializer.Serialize<TValue>(TValue value, JsonSerializerOptions? options = null)", StringComparison.Ordinal)
            && d.Summary is { Length: > 0 });
        Assert.Single(api.Find("M:System.Text.Json.JsonSerializer.Serialize``1(``0,System.Text.Json.JsonSerializerOptions)"));
    }

    [Fact]
    public async Task External_members_are_decompiled_from_the_implementation_behind_a_reference_assembly()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = fixture.Snapshot.Solution.Projects.Single(p => p.Name == "Modern.Core(net10.0)");
        var compilation = (await project.GetCompilationAsync(ct))!;
        var round = compilation.GetTypeByMetadataName("System.Math")!.GetMembers("Round").OfType<IMethodSymbol>()
            .Single(m => m.Parameters is [{ Type.SpecialType: SpecialType.System_Decimal }, { Type.SpecialType: SpecialType.System_Int32 }]);

        var reference = MetadataSource.AssemblyPath(compilation, round)!;
        var decompiled = MetadataSource.Decompile(reference, round);

        Assert.StartsWith("Microsoft.NETCore.App.Ref ", MetadataSource.Describe(reference), StringComparison.Ordinal);
        Assert.NotNull(decompiled);
        Assert.Matches(@"^Microsoft\.NETCore\.App 10\.\d+\.\d+ \(installed runtime\) · System\.Private\.CoreLib\.dll$", decompiled.Assembly);
        Assert.Contains("public static decimal Round(decimal d, int decimals)", decompiled.Code, StringComparison.Ordinal);
        Assert.Contains("decimal.Round(d, decimals)", decompiled.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("using ", decompiled.Code, StringComparison.Ordinal);
    }
}

/// <summary>Package lists without a restore, from files written in a temp folder.</summary>
public sealed class PackageInventoryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("farol-packages-");

    [Fact]
    public void Packages_config_lists_every_package_and_what_requires_it()
    {
        var project = Write("App/App.csproj", "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\" />");
        Write("App/packages.config", """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="Farol.Sample.Client" version="1.0.0" targetFramework="net48" />
              <package id="Farol.Sample.Json" version="2.0.0" targetFramework="net48" />
            </packages>
            """);
        Write("packages/Farol.Sample.Client.1.0.0/Farol.Sample.Client.nuspec", """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>Farol.Sample.Client</id>
                <version>1.0.0</version>
                <authors>Farol</authors>
                <description>Sample.</description>
                <dependencies>
                  <group targetFramework=".NETFramework4.5">
                    <dependency id="Farol.Sample.Json" version="2.0.0" />
                  </group>
                </dependencies>
              </metadata>
            </package>
            """);

        var packages = PackageInventory.ReadProject(NuGetContext.Load(_root.FullName), "App", project, ["net48"], []);

        Assert.Equal(PackageStyle.PackagesConfig, packages.Style);
        Assert.EndsWith("packages.config", packages.VersionsFile, StringComparison.Ordinal);
        var client = Assert.Single(packages.Packages, p => p.Id == "Farol.Sample.Client");
        var json = Assert.Single(packages.Packages, p => p.Id == "Farol.Sample.Json");
        Assert.True(client.IsDirect);
        Assert.False(json.IsDirect);
        Assert.Equal(["Farol.Sample.Client"], json.Via);
        Assert.Equal(["net48"], json.Frameworks);
        Assert.StartsWith("1 of 2 package(s) not restored", packages.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Unrestored_package_references_fall_back_to_the_declared_and_central_versions()
    {
        Write("Directory.Packages.props", """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><PackageVersion Include="Farol.Sample.Json" Version="2.1.0" /></ItemGroup>
            </Project>
            """);
        var project = Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Farol.Sample.Json" />
                <PackageReference Include="Farol.Sample.Pinned" VersionOverride="3.0.0" />
              </ItemGroup>
            </Project>
            """);

        var packages = PackageInventory.ReadProject(NuGetContext.Load(_root.FullName), "Lib", project, ["net10.0"], []);

        Assert.Equal(PackageStyle.PackageReference, packages.Style);
        Assert.True(packages.CentralPackageManagement);
        Assert.StartsWith("not restored", packages.Problem, StringComparison.Ordinal);
        Assert.Equal(["Farol.Sample.Json 2.1.0", "Farol.Sample.Pinned 3.0.0"], packages.Packages.Select(p => $"{p.Id} {p.Version}"));
        Assert.All(packages.Packages, p => Assert.True(p.IsDirect));
    }

    [Theory]
    [InlineData("SerializeObject", "SerObj", true)]
    [InlineData("OrderCalculator", "OrdCalc", true)]
    [InlineData("DeserializeObject", "SerObj", false)]
    [InlineData("JsonConvert", "JC", true)]
    [InlineData("JsonConvert", "Json", false)]
    public void Camel_humps_match_consecutive_capitalized_chunks(string name, string query, bool expected) =>
        Assert.Equal(expected, PackageApi.CamelHumps(name, query));

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_root.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}

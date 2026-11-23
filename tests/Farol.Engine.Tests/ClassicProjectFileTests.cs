using Farol.Engine.Loading;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>Classic project files read as XML for a heuristic load: conditions, properties, wildcards, references.</summary>
public sealed class ClassicProjectFileTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("farol-classic-");

    [Theory]
    [InlineData(" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' ", true)]
    [InlineData(" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' ", false)]
    [InlineData("'$(Configuration)' == ''", false)]
    [InlineData("'$(Undefined)' == ''", true)]
    [InlineData("'$(Configuration)' != 'Release' And '$(Platform)' == 'AnyCPU'", true)]
    [InlineData("'$(Configuration)' == 'Release' Or !('$(Platform)' == 'x86')", true)]
    [InlineData("!Exists('missing.props')", true)]
    [InlineData("false", false)]
    [InlineData("'$(TargetFrameworkVersion)' >= 'v4.5'", true)]
    [InlineData("$([System.String]::Copy('x')) == 'x'", true)]
    [InlineData("'$([MSBuild]::IsOsPlatform(Windows))' == 'false'", true)]
    [InlineData("'$(Undefined)' != '' And '$(Undefined)' >= '11.0'", false)]
    [InlineData("Exists('missing.props') == 'true'", false)]
    [InlineData("' v4.7.2 ' >= 'v4.5'", true)]
    public void Conditions_are_evaluated_for_the_configuration_read(string condition, bool expected)
    {
        var evaluator = new ConditionEvaluator(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Configuration"] = "Debug", ["Platform"] = "AnyCPU", ["TargetFrameworkVersion"] = "v4.7.2" },
            _root.FullName);

        Assert.Equal(expected, evaluator.Evaluate(condition));
    }

    [Fact]
    public void A_project_without_AnyCPU_reads_its_first_Debug_configuration_and_undefined_properties_are_empty()
    {
        var path = Write("App/App.csproj", """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
                <Platform Condition=" '$(Platform)' == '' ">x86</Platform>
                <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
              </PropertyGroup>
              <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Debug|x64' ">
                <DefineConstants>$(DefineConstants);DEBUG;X64</DefineConstants>
              </PropertyGroup>
              <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Release|x64' ">
                <DefineConstants>RELEASE</DefineConstants>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="System" />
                <Reference Include="Newtonsoft.Json" />
                <Reference Include="Newtonsoft.Json, Version=13.0.0.0">
                  <HintPath>..\packages\Newtonsoft.Json.13.0.1\lib\net45\Newtonsoft.Json.dll</HintPath>
                </Reference>
                <Reference Include="..\lib\Vendor.dll" />
              </ItemGroup>
            </Project>
            """);

        var project = ClassicProjectFile.Read(path, solutionDirectory: null);

        Assert.Equal(("Debug", "x64"), (project.Configuration, project.Platform));
        Assert.Equal(";DEBUG;X64", project.Property("DefineConstants"));
        Assert.Equal(["System", "Newtonsoft.Json", "Vendor"], project.References.Select(r => r.Name));
        Assert.EndsWith("Newtonsoft.Json.dll", project.References[1].HintPath, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_root.FullName, "lib", "Vendor.dll"), project.References[2].HintPath);
    }

    [Fact]
    public void Wildcards_exclude_and_remove_pick_the_compiled_files()
    {
        Write("Lib/A.cs", "class A { }");
        Write("Lib/Sub/B.cs", "class B { }");
        Write("Lib/obj/Debug/Gen.cs", "class Gen { }");
        Write("Lib/Old/C.cs", "class C { }");
        Write("Shared/Link.cs", "class L { }");
        var path = Write("Lib/Lib.csproj", """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <Compile Include="**\*.cs" Exclude="**\obj\**" />
                <Compile Remove="Old\*.cs" />
                <Compile Include="..\Shared\Link.cs"><Link>Common\Link.cs</Link></Compile>
              </ItemGroup>
              <Choose>
                <When Condition=" '$(Configuration)' == 'Release' "><ItemGroup><Compile Include="Release.cs" /></ItemGroup></When>
                <Otherwise><ItemGroup><Compile Include="Debug.cs" /></ItemGroup></Otherwise>
              </Choose>
            </Project>
            """);

        var project = ClassicProjectFile.Read(path, solutionDirectory: null);

        var compiled = project.Compile.ToDictionary(c => Path.GetRelativePath(Path.Combine(_root.FullName, "Lib"), c.Path).Replace('\\', '/'), c => c.Folders);
        Assert.Equal(["../Shared/Link.cs", "A.cs", "Debug.cs", "Sub/B.cs"], compiled.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["Common"], compiled["../Shared/Link.cs"]);
        Assert.Equal(["Sub"], compiled["Sub/B.cs"]);
    }

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

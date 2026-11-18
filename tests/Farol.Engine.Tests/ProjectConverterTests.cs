using Farol.Core;
using Farol.Engine.Conversion;
using Xunit;

namespace Farol.Engine.Tests;

/// <summary>Spec 002, AC-66/67: the project file rewrite keeps what the build depends on, or refuses.</summary>
public sealed class ProjectConverterTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("farol-convert-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Project => Path.Combine(_root.FullName, "App", "App.csproj");

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Classic(string propertyGroups, string itemGroups) => Write("App/App.csproj", $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <OutputType>Library</OutputType>
            <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
          </PropertyGroup>
          {propertyGroups}
          <ItemGroup>
            <Compile Include="Class1.cs" />
          </ItemGroup>
          {itemGroups}
          <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
        </Project>
        """);

    private static void Has(string text, string expected) => Assert.True(text.Contains(expected, StringComparison.Ordinal), $"Missing: {expected}\n{text}");

    [Fact]
    public void Keeps_extra_constants_and_a_custom_output_path_and_drops_the_defaults()
    {
        Write("App/Class1.cs", "class C {}");
        Classic("""
            <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' ">
              <DefineConstants>DEBUG;TRACE;LEGACY</DefineConstants>
              <OutputPath>..\build\</OutputPath>
              <Optimize>false</Optimize>
              <Prefer32Bit>true</Prefer32Bit>
            </PropertyGroup>
            <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' ">
              <DefineConstants>TRACE</DefineConstants>
              <OutputPath>bin\Release\</OutputPath>
              <Optimize>true</Optimize>
            </PropertyGroup>
            """, string.Empty);

        var conversion = ProjectConverter.Convert(Project, ConversionKind.SdkStyle);

        Has(conversion.NewText, "<TargetFramework>net472</TargetFramework>");
        Has(conversion.NewText, "<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>");
        Has(conversion.NewText, "<DefineConstants>DEBUG;TRACE;LEGACY</DefineConstants>");
        Has(conversion.NewText, "<OutputPath>..\\build\\</OutputPath>");
        Has(conversion.NewText, "<Prefer32Bit>true</Prefer32Bit>");
        Assert.DoesNotContain("<DefineConstants>TRACE</DefineConstants>", conversion.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain("bin\\Release\\", conversion.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain("<Optimize>", conversion.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public void Wildcard_compile_items_are_kept_as_written_without_the_default_globs()
    {
        Write("App/Class1.cs", "class C {}");
        Classic(string.Empty, """<ItemGroup><Compile Include="Generated\**\*.cs" /></ItemGroup>""");

        var conversion = ProjectConverter.Convert(Project, ConversionKind.SdkStyle);

        Has(conversion.NewText, "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
        Has(conversion.NewText, "<Compile Include=\"Generated\\**\\*.cs\" />");
        Has(conversion.NewText, "<Compile Include=\"Class1.cs\" />");
    }

    [Fact]
    public void Files_the_globs_would_add_are_removed_and_nested_obj_folders_count()
    {
        Write("App/Class1.cs", "class C {}");
        Write("App/Old/Unused.cs", "class U {}");
        Write("App/Sub/obj/Attributes.cs", "class A {}");
        Write("App/obj/Debug/Generated.cs", "class G {}");
        Classic(string.Empty, string.Empty);

        var conversion = ProjectConverter.Convert(Project, ConversionKind.SdkStyle);

        Has(conversion.NewText, "<Compile Remove=\"Old\\Unused.cs\" />");
        Has(conversion.NewText, "<Compile Remove=\"Sub\\obj\\Attributes.cs\" />");
        Assert.DoesNotContain("obj\\Debug\\Generated.cs", conversion.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain("<Compile Include=\"Class1.cs\"", conversion.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public void Linked_files_resources_conditions_and_unknown_items_are_kept()
    {
        Write("App/Class1.cs", "class C {}");
        Classic(string.Empty, """
            <ItemGroup>
              <None Include="..\Shared\settings.json"><Link>settings.json</Link><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
              <Resource Include="Images\logo.png" />
              <COMReference Include="Excel"><Guid>{00020813-0000-0000-C000-000000000046}</Guid></COMReference>
              <Folder Include="Images\" />
            </ItemGroup>
            <ItemGroup Condition=" '$(Configuration)' == 'Debug' ">
              <Reference Include="Diagnostics.Helpers"><HintPath>..\lib\Diagnostics.Helpers.dll</HintPath><Private>False</Private></Reference>
            </ItemGroup>
            """);

        var conversion = ProjectConverter.Convert(Project, ConversionKind.SdkStyle);

        Has(conversion.NewText, "<None Include=\"..\\Shared\\settings.json\">");
        Has(conversion.NewText, "<Resource Include=\"Images\\logo.png\" />");
        Has(conversion.NewText, "<COMReference Include=\"Excel\">");
        Has(conversion.NewText, "<Reference Include=\"Diagnostics.Helpers\" Condition=\" '$(Configuration)' == 'Debug' \">");
        Has(conversion.NewText, "<Private>False</Private>");
        Assert.DoesNotContain("<Folder", conversion.NewText, StringComparison.Ordinal);
        Assert.Contains(conversion.Notes, n => n.Contains("COMReference", StringComparison.Ordinal));
    }

    [Fact]
    public void Package_paths_through_the_solution_dir_are_recognised()
    {
        Write("App/Class1.cs", "class C {}");
        Write("App/packages.config", """
            <?xml version="1.0" encoding="utf-8"?>
            <packages><package id="Dapper" version="1.50.2" targetFramework="net472" /></packages>
            """);
        Classic(string.Empty, """
            <ItemGroup>
              <Reference Include="Dapper"><HintPath>$(SolutionDir)packages\Dapper.1.50.2\lib\net451\Dapper.dll</HintPath></Reference>
              <None Include="packages.config" />
            </ItemGroup>
            """);

        var conversion = ProjectConverter.Convert(Project, ConversionKind.PackageReference);

        Has(conversion.NewText, "<PackageReference Include=\"Dapper\" Version=\"1.50.2\" />");
        Assert.DoesNotContain("Dapper.dll", conversion.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain("packages.config", conversion.NewText, StringComparison.Ordinal);
        Assert.Equal([Path.Combine(_root.FullName, "App", "packages.config")], conversion.DeletedFiles);
    }

    [Fact]
    public void Choose_blocks_and_central_package_management_are_refused()
    {
        Write("App/Class1.cs", "class C {}");
        Classic("""<Choose><When Condition="true"><PropertyGroup><X>1</X></PropertyGroup></When></Choose>""", string.Empty);
        Assert.Contains("Choose/When", Assert.Throws<FarolException>(() => ProjectConverter.Convert(Project, ConversionKind.SdkStyle)).Message, StringComparison.Ordinal);

        Classic(string.Empty, string.Empty);
        Write("App/packages.config", """<packages><package id="Dapper" version="1.50.2" /></packages>""");
        Write("Directory.Packages.props", "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        Assert.Contains("centrally", Assert.Throws<FarolException>(() => ProjectConverter.Convert(Project, ConversionKind.PackageReference)).Message, StringComparison.Ordinal);
    }
}

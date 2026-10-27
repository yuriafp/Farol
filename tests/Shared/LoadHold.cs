namespace Farol.Testing;

/// <summary>
/// Holds a workspace load inside its design-time build, before it reads any source, until <see cref="Release"/>: the
/// MSBuild property it adds (<see cref="Property"/>) imports a target that waits for a file. Tests can then change
/// files while the workspace is loading, as an agent does while a large solution loads.
/// </summary>
public sealed class LoadHold : IDisposable
{
    private const string Targets = """
        <Project>
          <UsingTask TaskName="FarolWaitForFile" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
            <ParameterGroup>
              <FilePath ParameterType="System.String" Required="true" />
            </ParameterGroup>
            <Task>
              <Code Type="Fragment" Language="cs"><![CDATA[
                var deadline = System.DateTime.UtcNow.AddMinutes(2);
                while (!System.IO.File.Exists(FilePath) && System.DateTime.UtcNow < deadline)
                {
                    System.Threading.Thread.Sleep(50);
                }
              ]]></Code>
            </Task>
          </UsingTask>
          <Target Name="FarolHoldLoad" BeforeTargets="CoreCompile">
            <Touch Files="$(MSBuildThisFileDirectory)started" AlwaysCreate="true" />
            <FarolWaitForFile FilePath="$(MSBuildThisFileDirectory)release" />
          </Target>
        </Project>
        """;

    private readonly string _directory;

    private LoadHold(string directory)
    {
        _directory = directory;
    }

    /// <summary>The global MSBuild property to load with, e.g. through FarolEngineOptions.MSBuildProperties.</summary>
    public KeyValuePair<string, string> Property => new("CustomAfterMicrosoftCommonTargets", Path.Combine(_directory, "hold.targets"));

    public static LoadHold Create()
    {
        var directory = Directory.CreateTempSubdirectory("farol-hold-").FullName;
        File.WriteAllText(Path.Combine(directory, "hold.targets"), Targets);
        return new LoadHold(directory);
    }

    /// <summary>Waits until a design-time build of the load reaches the hold: the load has started and read no source yet.</summary>
    public async Task WaitUntilHeldAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!File.Exists(Path.Combine(_directory, "started")))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The load never reached the hold.");
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    public void Release() => File.WriteAllText(Path.Combine(_directory, "release"), string.Empty);

    public void Dispose()
    {
        Release();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A build host can still hold the targets file for a moment; temp cleanup is best effort.
        }
    }
}

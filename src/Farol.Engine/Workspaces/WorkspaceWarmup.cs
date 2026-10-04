using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Farol.Engine.Workspaces;

/// <summary>
/// Right after a load, builds in the background what the first queries would otherwise wait for: every project's
/// compilation, and the syntax and inheritance indexes that reference and implementation searches use. Requests share
/// all of it; one that arrives first builds what it needs itself.
/// </summary>
internal static class WorkspaceWarmup
{
    public static async Task RunAsync(Solution solution, CancellationToken cancellationToken)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(solution.Projects, options, async (project, token) => await project.GetCompilationAsync(token));

        // An interface of the project most others depend on: searching for its implementations and uses indexes every
        // project that could reference it.
        var graph = solution.GetProjectDependencyGraph();
        var hub = solution.ProjectIds.MaxBy(id => graph.GetProjectsThatTransitivelyDependOnThisProject(id).Count());
        if (hub is null || solution.GetProject(hub) is not { } project || await project.GetCompilationAsync(cancellationToken) is not { } compilation)
        {
            return;
        }

        var anInterface = compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type, cancellationToken)
            .OfType<INamedTypeSymbol>()
            .FirstOrDefault(t => t.TypeKind == TypeKind.Interface);
        if (anInterface is not null)
        {
            await SymbolFinder.FindImplementationsAsync(anInterface, solution, cancellationToken: cancellationToken);
            await SymbolFinder.FindReferencesAsync(anInterface, solution, cancellationToken);
        }
    }
}

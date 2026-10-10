using Farol.Core;
using Farol.Core.Paths;
using Farol.Core.Text;
using Farol.Engine.Navigation;
using Farol.Engine.Workspaces;

namespace Farol.Tools.Features.Navigation;

/// <summary>The "symbol" parameter shared by navigation tools.</summary>
internal static class SymbolArgument
{
    public const string Description =
        "The symbol: a name (OrderCalculator), a dotted name (OrderCalculator.GetTotal), a documentation comment ID from a previous result " +
        "(M:Ns.Type.Method(System.Int32)), or a source position (path/File.cs:42 or path/File.cs:42:17, relative to the root as responses write paths).";

    /// <summary>
    /// Resolves the argument. An ambiguous name is an answer, not an error: the returned text lists the
    /// candidates with the IDs to call again with.
    /// </summary>
    public static async Task<(SymbolCandidate? Candidate, string? Ambiguity)> ResolveAsync(
        WorkspaceSnapshot snapshot, PathSandbox paths, string symbol, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new FarolException(ErrorCodes.InvalidArgument, "'symbol' is empty.", "Pass a name, a dotted name, a documentation comment ID or a path:line position.");
        }

        // A source position is a path argument like any other: it must stay inside the trusted directories.
        var candidates = await SymbolLocator.ResolveAsync(snapshot, path => paths.Resolve(path, snapshot.Target.Directory), symbol, cancellationToken);
        if (candidates.Count == 1)
        {
            return (candidates[0], null);
        }

        if (candidates.Count == 0)
        {
            throw new FarolException(
                ErrorCodes.SymbolNotFound,
                $"No symbol matches '{symbol}'.",
                "Search with dotnet_find_symbols, or pass a documentation comment ID or a path:line position.");
        }

        var text = new ResponseBuilder(TokenBudget.DefaultTokens);
        text.Line($"'{symbol}' matches {candidates.Count} symbols. Call again with the id of the one you mean:");
        text.List("candidates", candidates, c => NavigationText.Candidate(c, snapshot, paths.Root), continuation: _ => "use a more specific name");
        return (null, text.ToString());
    }
}

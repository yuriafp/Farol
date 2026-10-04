using System.Globalization;
using Farol.Evals;

// The eval suite of spec 001's exit criterion (evals/README.md): each task runs in Claude Code with Farol's plugin and
// without it (grep + build), from fresh workspaces, and checks decide every run.
//   Farol.Evals run [--tasks <ids or prefixes>] [--arms farol,baseline] [--runs 3] [--model <model>] [--concurrency 2]
//                   [--out <directory>] [--max-cost-usd <usd>] [--keep-workspaces true]
//   Farol.Evals validate [--tasks ...]      proves each task's checks: they fail on an untouched workspace and pass on
//                                           the reference answer and change
//   Farol.Evals report --out <directory>    rewrites summary.md from the runs there

Console.OutputEncoding = System.Text.Encoding.UTF8;
var command = args.FirstOrDefault();
var options = Arguments.Parse(args.Skip(1).ToArray());
var root = Arguments.RepositoryRoot();
var home = options.GetValueOrDefault("home") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Farol", "evals");
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    return command switch
    {
        "run" => await Suite.RunAsync(root, home, options, cancel.Token),
        "validate" => await Suite.ValidateAsync(root, home, options, cancel.Token),
        "report" when options.GetValueOrDefault("out") is { } directory => Suite.Report(Path.GetFullPath(directory)),
        _ => Arguments.Usage(),
    };
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested)
{
    Console.Error.WriteLine("Interrupted: finished runs are kept, and `run --out` with the same directory resumes.");
    return 130;
}


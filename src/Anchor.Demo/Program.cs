using Anchor.Core.Services;

var root = FindRepositoryRoot();
var replayRoot = Path.Combine(root, "demo", "replay");
var requested = args.Length == 0 ? "all" : args[0];
var files = string.Equals(requested, "all", StringComparison.OrdinalIgnoreCase)
    ? Directory.GetFiles(replayRoot, "*.jsonl").Order(StringComparer.Ordinal).ToArray()
    : [Path.Combine(replayRoot, requested.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? requested : $"{requested}.jsonl")];

foreach (var file in files)
{
    if (!File.Exists(file))
    {
        Console.Error.WriteLine($"Scenario not found: {file}");
        Environment.ExitCode = 2;
        continue;
    }

    var report = await ReplayScenarioRunner.RunFileAsync(file);
    Console.WriteLine($"\n{report.Scenario}");
    Console.WriteLine(new string('─', report.Scenario.Length));
    foreach (var step in report.Steps)
    {
        Console.WriteLine($"{step.AtSeconds,4}s  {step.State,-11}  {step.Intervention,-14}  {(step.ExpectationMatched ? "✓" : "✗")}");
        if (!step.ExpectationMatched)
        {
            Console.WriteLine($"       {step.ExpectationMessage}");
            Environment.ExitCode = 1;
        }
    }
    Console.WriteLine($"Final: {report.FinalState}; interruptions: {report.Progress.InterruptionCount}; focused: {report.Progress.FocusedSeconds:0}s");
}

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Anchor.slnx")))
        {
            return directory.FullName;
        }
    }

    throw new DirectoryNotFoundException("Could not locate Anchor.slnx.");
}

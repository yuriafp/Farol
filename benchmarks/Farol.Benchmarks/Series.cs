namespace Farol.Benchmarks;

/// <summary>Timed calls of one kind, with the spec 001 target their 95th percentile must stay under.</summary>
internal sealed class Series(string name, double targetMilliseconds)
{
    private readonly List<Sample> _samples = [];

    public string Name => name;

    public double TargetMilliseconds => targetMilliseconds;

    public IReadOnlyList<Sample> Samples => _samples;

    public double P50 => Percentile(0.50);

    public double P95 => Percentile(0.95);

    public double Max => _samples.Count == 0 ? 0 : _samples.Max(s => s.Milliseconds);

    public bool Passed => _samples.Count > 0 && P95 < targetMilliseconds;

    public void Add(string label, TimeSpan elapsed) => _samples.Add(new Sample(label, elapsed.TotalMilliseconds));

    public IEnumerable<Sample> Slowest(int count) => _samples.OrderByDescending(s => s.Milliseconds).Take(count);

    /// <summary>Nearest-rank percentile: the smallest sample with at least <paramref name="fraction"/> of the samples at or below it.</summary>
    private double Percentile(double fraction)
    {
        if (_samples.Count == 0)
        {
            return 0;
        }

        var sorted = _samples.Select(s => s.Milliseconds).Order().ToArray();
        var rank = (int)Math.Ceiling(fraction * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}

internal sealed record Sample(string Label, double Milliseconds);

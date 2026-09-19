namespace Anchor.Core.Services;

public sealed class TemporalEvidenceBuffer
{
    private readonly Queue<double> _values = new();

    public TemporalEvidenceSummary Add(double value)
    {
        _values.Enqueue(Math.Clamp(value, 0, 1));
        while (_values.Count > 30)
        {
            _values.Dequeue();
        }

        var snapshot = _values.ToArray();
        return new TemporalEvidenceSummary(
            Current: snapshot[^1],
            OneSecond: snapshot[^1],
            FiveSecond: snapshot.TakeLast(5).Average(),
            ThirtySecond: snapshot.Average());
    }
}

public sealed record TemporalEvidenceSummary(
    double Current,
    double OneSecond,
    double FiveSecond,
    double ThirtySecond);

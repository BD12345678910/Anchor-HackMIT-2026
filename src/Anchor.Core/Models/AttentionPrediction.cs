namespace Anchor.Core.Models;

public sealed record AttentionPrediction(
    AttentionState State,
    double Confidence,
    double DistractionProbability,
    IReadOnlyList<string> ReasonCodes)
{
    public static AttentionPrediction Create(
        AttentionState state,
        double confidence,
        double distractionProbability,
        IReadOnlyList<string>? reasonCodes = null) =>
        new(
            state,
            ClampScore(confidence),
            ClampScore(distractionProbability),
            reasonCodes?.Where(static code => !string.IsNullOrWhiteSpace(code)).Distinct().ToArray()
                ?? []);

    private static double ClampScore(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}

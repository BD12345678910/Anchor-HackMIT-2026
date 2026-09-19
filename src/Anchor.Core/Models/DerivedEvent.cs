using Anchor.Core.Services;

namespace Anchor.Core.Models;

public sealed record DerivedEvent(
    Guid Id,
    Guid SessionId,
    DateTimeOffset Timestamp,
    string Source,
    string Type,
    IReadOnlyDictionary<string, string> Features)
{
    public static DerivedEvent Create(
        Guid sessionId,
        DateTimeOffset timestamp,
        string source,
        string type,
        IReadOnlyDictionary<string, string>? features = null)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        return new DerivedEvent(
            Guid.NewGuid(),
            sessionId,
            timestamp,
            source.Trim()[..Math.Min(source.Trim().Length, 64)],
            type.Trim()[..Math.Min(type.Trim().Length, 64)],
            SensitiveTextRedactor.RedactFeatures(features));
    }
}

public sealed record ProgressSnapshot(
    Guid SessionId,
    double FocusedSeconds,
    int InterruptionCount,
    double RecoverySeconds,
    DateTimeOffset LastUpdatedAt);

using Anchor.Core.Models;

namespace Anchor.Core.Services;

public sealed class ContextCapsuleManager
{
    private readonly Guid _sessionId;
    private readonly string _taskTitle;
    private ContextObservation? _lastConfidentObservation;

    public ContextCapsuleManager(Guid sessionId, string taskTitle)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(taskTitle);
        _sessionId = sessionId;
        _taskTitle = taskTitle.Trim();
    }

    public ContextCapsule? Observe(ContextObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.IsSensitiveField || observation.Confidence < 0.65)
        {
            return null;
        }

        _lastConfidentObservation = observation with
        {
            Application = Bound(observation.Application, 120),
            DocumentIdentity = Bound(SensitiveTextRedactor.Redact(observation.DocumentIdentity), 512),
            Location = Bound(SensitiveTextRedactor.Redact(observation.Location), 512),
            LastAction = Bound(SensitiveTextRedactor.Redact(observation.LastAction), 512),
            NextAction = Bound(SensitiveTextRedactor.Redact(observation.NextAction), 512),
            SelectedText = BoundNullable(SensitiveTextRedactor.Redact(observation.SelectedText), 1_024),
            RestoreTarget = BoundNullable(SensitiveTextRedactor.Redact(observation.RestoreTarget), 1_024),
            Confidence = Math.Clamp(observation.Confidence, 0, 1)
        };

        return CreateCapsule(DistractionReason.IdleReturn);
    }

    public ContextCapsule Freeze(DistractionReason reason) =>
        _lastConfidentObservation is null
            ? throw new InvalidOperationException("No safe context anchor is available.")
            : CreateCapsule(reason);

    private ContextCapsule CreateCapsule(DistractionReason reason)
    {
        var anchor = _lastConfidentObservation!;
        return new ContextCapsule(
            _sessionId,
            DateTimeOffset.UtcNow,
            _taskTitle,
            anchor.Application,
            anchor.DocumentIdentity,
            anchor.Location,
            anchor.LastAction,
            anchor.NextAction,
            anchor.SelectedText,
            anchor.RestoreTarget,
            reason);
    }

    private static string Bound(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim()[..Math.Min(value.Trim().Length, length)];

    private static string? BoundNullable(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
}

using Anchor.Core.Models;

namespace Anchor.Core.Services;

public sealed class ProgressTracker
{
    private readonly Guid _sessionId;
    private string? _lastAttentionState;
    private DateTimeOffset? _lastTimestamp;
    private DateTimeOffset? _interruptionStartedAt;
    private double _focusedSeconds;
    private double _recoverySeconds;
    private int _interruptionCount;

    public ProgressTracker(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        }

        _sessionId = sessionId;
    }

    public ProgressSnapshot Apply(DerivedEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.SessionId != _sessionId)
        {
            throw new ArgumentException("Event belongs to a different session.", nameof(item));
        }

        if (_lastTimestamp is not null && item.Timestamp < _lastTimestamp)
        {
            throw new ArgumentException("Events must be applied in timestamp order.", nameof(item));
        }

        if (_lastTimestamp is not null && _lastAttentionState == "focused")
        {
            _focusedSeconds += (item.Timestamp - _lastTimestamp.Value).TotalSeconds;
        }

        if (item.Source == "attention")
        {
            if (item.Type == "distracted" && _lastAttentionState != "distracted")
            {
                _interruptionCount++;
                _interruptionStartedAt = item.Timestamp;
            }
            else if (item.Type == "focused" && _interruptionStartedAt is not null)
            {
                _recoverySeconds += (item.Timestamp - _interruptionStartedAt.Value).TotalSeconds;
                _interruptionStartedAt = null;
            }

            _lastAttentionState = item.Type;
        }

        _lastTimestamp = item.Timestamp;
        return new ProgressSnapshot(
            _sessionId,
            _focusedSeconds,
            _interruptionCount,
            _recoverySeconds,
            item.Timestamp);
    }
}

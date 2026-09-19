namespace Anchor.Core.Models;

public sealed record ContextCapsule(
    Guid SessionId,
    DateTimeOffset CapturedAt,
    string Application,
    string Location,
    string LastAction,
    string NextAction,
    string? RestoreTarget);

using System.Text.Json;
using Anchor.Core.Services;

namespace Anchor.Infrastructure.Browser;

public sealed record BrowserContextSnapshot(
    string Origin,
    string Title,
    double Progress,
    int ParagraphIndex,
    string? StuckPhrase,
    DateTimeOffset UpdatedAt);

public sealed class BrowserContextTracker
{
    private readonly Lock _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private BrowserContextSnapshot? _snapshot;

    public BrowserContextTracker(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool Apply(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("source", out var source)
                || source.GetString() != "anchor-content"
                || !root.TryGetProperty("event", out var browserEvent)
                || !browserEvent.TryGetProperty("type", out var typeElement))
            {
                return false;
            }
            var type = typeElement.GetString();
            lock (_gate)
            {
                if (type == "page-context")
                {
                    var origin = ReadString(browserEvent, "origin");
                    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                        || uri.Scheme is not ("http" or "https"))
                    {
                        return false;
                    }
                    _snapshot = new BrowserContextSnapshot(
                        uri.GetLeftPart(UriPartial.Authority),
                        Bound(SensitiveTextRedactor.Redact(ReadString(browserEvent, "title")), 240),
                        0,
                        0,
                        null,
                        _clock());
                    return true;
                }
                if (_snapshot is null) return false;
                if (type is "reading-progress" or "reading-skip")
                {
                    var progress = ReadDouble(browserEvent, type == "reading-skip" ? "to" : "progress");
                    var paragraph = ReadInt(browserEvent, "paragraphIndex");
                    _snapshot = _snapshot with
                    {
                        Progress = Math.Clamp(progress, 0, 1),
                        ParagraphIndex = Math.Max(0, paragraph),
                        UpdatedAt = _clock()
                    };
                    return true;
                }
                if (type == "stuck-phrase")
                {
                    _snapshot = _snapshot with
                    {
                        StuckPhrase = Bound(SensitiveTextRedactor.Redact(ReadString(browserEvent, "phrase")), 180),
                        UpdatedAt = _clock()
                    };
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }
        return false;
    }

    public BrowserContextSnapshot? Snapshot()
    {
        lock (_gate) return _snapshot;
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static double ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetDouble(out var number) ? number : 0;

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static string Bound(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim()[..Math.Min(value.Trim().Length, maximum)];
}

using System.Text.Json;

namespace Anchor_Desktop.Services;

public sealed record ToolPreferences(
    bool VisualFilter = true,
    bool BlurImages = true,
    bool HideFutureText = true,
    bool SuppressAnimations = false,
    bool AudioShield = false,
    bool PointerGuard = false,
    bool GazeSpotlight = false,
    bool WindowFirewall = false,
    bool ReducedMotion = false);

/// <summary>Plain JSON store for non-secret tool toggles; DeepSeek settings stay in their encrypted store.</summary>
public sealed class ToolPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public ToolPreferencesStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<ToolPreferences?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ToolPreferences>(
                await File.ReadAllTextAsync(_path, cancellationToken),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(ToolPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Preferences path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(preferences, JsonOptions), cancellationToken);
        File.Move(temporary, _path, overwrite: true);
    }
}

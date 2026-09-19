using Anchor.Core.Models;
using Anchor.Infrastructure.Worker;

namespace Anchor.Infrastructure.Recording;

public sealed class StudyRecordingClient
{
    private readonly InferenceWorkerClient _worker;
    private readonly string _selectedRoot;

    public StudyRecordingClient(InferenceWorkerClient worker, string userSelectedOutputFolder)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        ArgumentException.ThrowIfNullOrWhiteSpace(userSelectedOutputFolder);
        _selectedRoot = Path.GetFullPath(userSelectedOutputFolder);
    }

    public Task<(StudyRecordingManifest? Manifest, string? Error)> StartAsync(
        TrialMode trialMode,
        string participantCode,
        string? subfolder = null,
        int displayIndex = 1,
        int fps = 15,
        CancellationToken cancellationToken = default)
    {
        var output = ResolveOutputDirectory(_selectedRoot, subfolder);
        Directory.CreateDirectory(output);
        return _worker.StartRecordingAsync(
            output,
            trialMode,
            participantCode,
            displayIndex,
            fps,
            cancellationToken);
    }

    public Task<StudyRecordingStatus> AppendEventAsync(
        IReadOnlyDictionary<string, object?> item,
        CancellationToken cancellationToken = default) =>
        _worker.AppendRecordingEventAsync(item, cancellationToken);

    public Task<StudyRecordingStatus> AppendSampleAsync(
        StudyRecordingSample sample,
        CancellationToken cancellationToken = default) =>
        _worker.AppendRecordingSampleAsync(sample, cancellationToken);

    public Task<StudyRecordingStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        _worker.GetRecordingStatusAsync(cancellationToken);

    public Task<StudyRecordingStatus> StopAsync(CancellationToken cancellationToken = default) =>
        _worker.StopRecordingAsync(cancellationToken);

    public static string ResolveOutputDirectory(string selectedRoot, string? subfolder)
    {
        var root = Path.GetFullPath(selectedRoot);
        var candidate = string.IsNullOrWhiteSpace(subfolder)
            ? root
            : Path.GetFullPath(Path.Combine(root, subfolder));
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Recording output must remain inside the user-selected folder.");
        }
        return candidate;
    }
}

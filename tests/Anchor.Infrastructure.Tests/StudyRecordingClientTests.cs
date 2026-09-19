using Anchor.Infrastructure.Recording;

namespace Anchor.Infrastructure.Tests;

public sealed class StudyRecordingClientTests
{
    [Fact]
    public void Output_is_restricted_to_the_user_selected_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"anchor-study-{Guid.NewGuid():N}");

        var accepted = StudyRecordingClient.ResolveOutputDirectory(root, "session-1");

        Assert.StartsWith(Path.GetFullPath(root), accepted, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<UnauthorizedAccessException>(() =>
            StudyRecordingClient.ResolveOutputDirectory(root, Path.Combine("..", "outside")));
    }
}

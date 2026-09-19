namespace Anchor.Core.Models;

public sealed record GazeConfiguration(
    int CameraIndex = 0,
    bool Mirror = true,
    int RotationDegrees = 0,
    double OffsetX = 0,
    double OffsetY = 0,
    double Smoothing = 0.65,
    double Sensitivity = 1,
    double MinimumConfidence = 0.45);

public sealed record GazeConfigurationResult(
    bool Accepted,
    string Error,
    GazeConfiguration? Configuration);

public sealed record GazeSample(
    double? X,
    double? Y,
    double Confidence,
    bool FacePresent,
    DateTimeOffset Timestamp,
    double Yaw,
    double Pitch,
    double Roll,
    byte[] PreviewJpeg,
    string UnavailableReason)
{
    public bool Available => X.HasValue && Y.HasValue;

    public static GazeSample Unavailable(string reason) => new(
        null,
        null,
        0,
        false,
        DateTimeOffset.MinValue,
        0,
        0,
        0,
        [],
        reason);
}

public sealed record CameraDevice(int Index, string Name);

public sealed record GazeStatus(bool Running, string Error);

public sealed record CalibrationProgress(bool Accepted, int SampleCount, string Error);

public sealed record CalibrationResult(
    bool Accepted,
    int SampleCount,
    int InlierCount,
    double MedianError,
    string Error);


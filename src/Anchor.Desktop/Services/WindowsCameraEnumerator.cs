using Anchor.Core.Models;
using Windows.Devices.Enumeration;

namespace Anchor_Desktop.Services;

/// <summary>
/// Lists video capture devices known to Windows so the UI can show real camera names and
/// distinguish "no camera attached" from "the vision worker could not open the camera".
/// </summary>
public static class WindowsCameraEnumerator
{
    public static async Task<IReadOnlyList<CameraDevice>> ListAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return devices
            .Select((device, index) => new CameraDevice(index, string.IsNullOrWhiteSpace(device.Name) ? $"Camera {index + 1}" : device.Name))
            .ToArray();
    }

    public static IReadOnlyList<CameraDevice> Merge(
        IReadOnlyList<CameraDevice> windowsDevices,
        IReadOnlyList<CameraDevice> openableDevices)
    {
        if (openableDevices.Count == 0)
        {
            return windowsDevices;
        }

        return openableDevices
            .Select(device =>
            {
                var named = windowsDevices.FirstOrDefault(item => item.Index == device.Index);
                return named is null || windowsDevices.Count != openableDevices.Count
                    ? device
                    : new CameraDevice(device.Index, named.Name);
            })
            .ToArray();
    }
}

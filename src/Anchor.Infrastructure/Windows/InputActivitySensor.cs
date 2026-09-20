using System.Globalization;
using System.Runtime.InteropServices;
using Anchor.Core.Models;

namespace Anchor.Infrastructure.Windows;

public enum VirtualKeyCategory
{
    Letter,
    Digit,
    Navigation,
    Editing,
    Modifier,
    Function,
    Other
}

public sealed class InputActivitySensor
{
    private const uint RidInput = 0x10000003;
    private const ushort RimTypeMouse = 0;
    private const ushort RimTypeKeyboard = 1;
    private const ushort RiMouseWheel = 0x0400;
    private const ushort RiMouseButtonDown = 0x0001 | 0x0004 | 0x0010 | 0x0040 | 0x0100;
    private const ushort WmKeyDown = 0x0100;
    private const ushort WmSysKeyDown = 0x0104;

    private readonly Guid _sessionId;
    private readonly object _gate = new();
    private readonly int[] _keyCounts = new int[Enum.GetValues<VirtualKeyCategory>().Length];
    private double _mouseDistance;
    private double _mouseNetX;
    private double _mouseNetY;
    private int _mouseDirectionChanges;
    private int _mouseClicks;
    private int _lastMouseSignX;
    private int _lastMouseSignY;
    private int _scrollReversals;
    private int _scrollNotches;
    private int _lastScrollDirection;

    public InputActivitySensor(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        }

        _sessionId = sessionId;
    }

    /// <summary>
    /// Accumulates both the length of the path the cursor took and where it actually ended up:
    /// a long path with a short net displacement is circling rather than pointing at something.
    /// </summary>
    public void RecordMouseDelta(int deltaX, int deltaY)
    {
        lock (_gate)
        {
            _mouseDistance += Math.Sqrt(((double)deltaX * deltaX) + ((double)deltaY * deltaY));
            _mouseNetX += deltaX;
            _mouseNetY += deltaY;
            _mouseDirectionChanges += CountDirectionChange(Math.Sign(deltaX), ref _lastMouseSignX)
                + CountDirectionChange(Math.Sign(deltaY), ref _lastMouseSignY);
        }
    }

    public void RecordMouseClick()
    {
        lock (_gate)
        {
            _mouseClicks++;
        }
    }

    private static int CountDirectionChange(int sign, ref int lastSign)
    {
        if (sign == 0)
        {
            return 0;
        }

        var changed = lastSign != 0 && sign != lastSign;
        lastSign = sign;
        return changed ? 1 : 0;
    }

    public void RecordKeyDown(VirtualKeyCategory category)
    {
        lock (_gate)
        {
            _keyCounts[(int)category]++;
        }
    }

    public void RecordScrollDelta(int delta)
    {
        var direction = Math.Sign(delta);
        if (direction == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_lastScrollDirection != 0 && direction != _lastScrollDirection)
            {
                _scrollReversals++;
            }

            // One notch is WHEEL_DELTA; a fast flick reports several at once.
            _scrollNotches += Math.Max(1, Math.Abs(delta) / 120);
            _lastScrollDirection = direction;
        }
    }

    public DerivedEvent Snapshot(DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            var keyCount = _keyCounts.Sum();
            var features = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mouse_distance"] = Math.Round(_mouseDistance).ToString(CultureInfo.InvariantCulture),
                ["mouse_net_distance"] = Math.Round(Math.Sqrt((_mouseNetX * _mouseNetX) + (_mouseNetY * _mouseNetY)))
                    .ToString(CultureInfo.InvariantCulture),
                ["mouse_direction_changes"] = _mouseDirectionChanges.ToString(CultureInfo.InvariantCulture),
                ["mouse_click_count"] = _mouseClicks.ToString(CultureInfo.InvariantCulture),
                ["key_count"] = keyCount.ToString(CultureInfo.InvariantCulture),
                ["key_letter_count"] = _keyCounts[(int)VirtualKeyCategory.Letter].ToString(CultureInfo.InvariantCulture),
                ["key_digit_count"] = _keyCounts[(int)VirtualKeyCategory.Digit].ToString(CultureInfo.InvariantCulture),
                ["key_navigation_count"] = _keyCounts[(int)VirtualKeyCategory.Navigation].ToString(CultureInfo.InvariantCulture),
                ["key_editing_count"] = _keyCounts[(int)VirtualKeyCategory.Editing].ToString(CultureInfo.InvariantCulture),
                ["key_modifier_count"] = _keyCounts[(int)VirtualKeyCategory.Modifier].ToString(CultureInfo.InvariantCulture),
                ["key_function_count"] = _keyCounts[(int)VirtualKeyCategory.Function].ToString(CultureInfo.InvariantCulture),
                ["key_other_count"] = _keyCounts[(int)VirtualKeyCategory.Other].ToString(CultureInfo.InvariantCulture),
                ["scroll_reversal_count"] = _scrollReversals.ToString(CultureInfo.InvariantCulture),
                ["scroll_notch_count"] = _scrollNotches.ToString(CultureInfo.InvariantCulture)
            };

            Array.Clear(_keyCounts);
            _mouseDistance = 0;
            _mouseNetX = 0;
            _mouseNetY = 0;
            _mouseDirectionChanges = 0;
            _mouseClicks = 0;
            _lastMouseSignX = 0;
            _lastMouseSignY = 0;
            _scrollReversals = 0;
            _scrollNotches = 0;
            _lastScrollDirection = 0;
            return DerivedEvent.Create(_sessionId, timestamp, "input", "activity", features);
        }
    }

    public static void RegisterRawInput(IntPtr windowHandle)
    {
        var devices = new[]
        {
            new RawInputDevice(0x01, 0x02, 0x00000100, windowHandle),
            new RawInputDevice(0x01, 0x06, 0x00000100, windowHandle)
        };
        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            throw new InvalidOperationException($"Raw Input registration failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    /// <summary>Records the input and returns the virtual key when it was a key-down.</summary>
    public ushort? ProcessRawInput(IntPtr rawInputHandle)
    {
        if (!TryReadRawInput(rawInputHandle, out var input))
        {
            return null;
        }

        if (input.Header.Type == RimTypeMouse)
        {
            RecordMouseDelta(input.Data.Mouse.LastX, input.Data.Mouse.LastY);
            if ((input.Data.Mouse.ButtonFlags & RiMouseWheel) != 0)
            {
                RecordScrollDelta((short)input.Data.Mouse.ButtonData);
            }
            else if ((input.Data.Mouse.ButtonFlags & RiMouseButtonDown) != 0)
            {
                RecordMouseClick();
            }
            return null;
        }

        if (input.Header.Type == RimTypeKeyboard
            && input.Data.Keyboard.Message is WmKeyDown or WmSysKeyDown)
        {
            var key = input.Data.Keyboard.VirtualKey;
            RecordKeyDown(CategorizeVirtualKey(key));
            if (PageKeyDirection(key) is { } direction)
            {
                RecordScrollDelta(direction * 120);
            }
            return key;
        }

        return null;
    }

    /// <summary>Returns the virtual key of a key-down raw input without recording activity.</summary>
    public static ushort? PeekKeyDown(IntPtr rawInputHandle)
    {
        if (!TryReadRawInput(rawInputHandle, out var input)
            || input.Header.Type != RimTypeKeyboard
            || input.Data.Keyboard.Message is not (WmKeyDown or WmSysKeyDown))
        {
            return null;
        }

        return input.Data.Keyboard.VirtualKey;
    }

    private static bool TryReadRawInput(IntPtr rawInputHandle, out RawInput input)
    {
        input = default;
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size == 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (GetRawInputData(rawInputHandle, RidInput, buffer, ref size, headerSize) != size)
            {
                return false;
            }

            input = Marshal.PtrToStructure<RawInput>(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Keys that move the document like the wheel does: page up/down and the arrows.</summary>
    public static int? PageKeyDirection(ushort key) => key switch
    {
        0x21 or 0x26 => -1,
        0x22 or 0x28 => 1,
        _ => null
    };

    private static VirtualKeyCategory CategorizeVirtualKey(ushort key) => key switch
    {
        >= 0x41 and <= 0x5A => VirtualKeyCategory.Letter,
        >= 0x30 and <= 0x39 => VirtualKeyCategory.Digit,
        >= 0x25 and <= 0x28 or 0x21 or 0x22 or 0x23 or 0x24 => VirtualKeyCategory.Navigation,
        0x08 or 0x09 or 0x0D or 0x2D or 0x2E => VirtualKeyCategory.Editing,
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C => VirtualKeyCategory.Modifier,
        >= 0x70 and <= 0x87 => VirtualKeyCategory.Function,
        _ => VirtualKeyCategory.Other
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        RawInputDevice[] devices,
        uint numberOfDevices,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        IntPtr data,
        ref uint size,
        uint headerSize);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RawInputDevice(
        ushort UsagePage,
        ushort Usage,
        uint Flags,
        IntPtr Target);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouse
    {
        public ushort Flags;
        public ushort ButtonFlags;
        public ushort ButtonData;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RawInputUnion
    {
        [FieldOffset(0)] public RawMouse Mouse;
        [FieldOffset(0)] public RawKeyboard Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInput
    {
        public RawInputHeader Header;
        public RawInputUnion Data;
    }
}

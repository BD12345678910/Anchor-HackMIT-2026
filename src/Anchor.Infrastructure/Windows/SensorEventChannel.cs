using System.Threading.Channels;
using Anchor.Core.Models;

namespace Anchor.Infrastructure.Windows;

public static class SensorEventChannel
{
    public static Channel<DerivedEvent> Create(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        return Channel.CreateBounded<DerivedEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }
}

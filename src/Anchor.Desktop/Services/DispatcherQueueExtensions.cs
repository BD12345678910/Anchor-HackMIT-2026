using Microsoft.UI.Dispatching;

namespace Anchor_Desktop.Services;

public static class DispatcherQueueExtensions
{
    public static Task EnqueueAsync(this DispatcherQueue dispatcher, Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception error)
                {
                    completion.SetException(error);
                }
            }))
        {
            completion.SetException(new InvalidOperationException("The UI dispatcher is unavailable."));
        }

        return completion.Task;
    }
}

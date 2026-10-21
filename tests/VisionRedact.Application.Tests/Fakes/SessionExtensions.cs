using VisionRedact.Application.Playback;

namespace VisionRedact.Application.Tests.Fakes;

internal static class SessionExtensions
{
    public static async Task WaitForStateAsync(this PlaybackSession session, PlaybackState state, TimeSpan? timeout = null)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, PlaybackStateChangedEventArgs e)
        {
            if (e.Current == state)
            {
                signal.TrySetResult();
            }
        }

        session.StateChanged += Handler;
        try
        {
            if (session.State == state)
            {
                return;
            }

            await signal.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));
        }
        finally
        {
            session.StateChanged -= Handler;
        }
    }
}

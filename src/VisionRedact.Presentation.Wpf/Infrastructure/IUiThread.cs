using System.Windows.Threading;

namespace VisionRedact.Presentation.Wpf.Infrastructure;

// the one door to the Dispatcher, for the view models and the frame presenter. background news (e.g.
// PlaybackSession.StateChanged) is posted here, no Dispatcher.Invoke all over the code.
// only Post, never Invoke: a worker that waits for the UI thread deadlocks when the UI thread waits for it
// (e.g. at shutdown)
public interface IUiThread
{
    /// <summary>True when the caller is already on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Queues the action on the UI thread and returns at once.</summary>
    void Post(Action action, DispatcherPriority priority = DispatcherPriority.Normal);
}

// the real one, on the app's Dispatcher
public sealed class DispatcherUiThread : IUiThread
{
    private readonly Dispatcher _dispatcher;

    // built by the container at startup, on the UI thread
    public DispatcherUiThread()
        : this(System.Windows.Application.Current.Dispatcher)
    {
    }

    public DispatcherUiThread(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public bool CheckAccess() => _dispatcher.CheckAccess();

    // any thread -> UI thread. BeginInvoke only queues it, the caller never blocks
    public void Post(Action action, DispatcherPriority priority = DispatcherPriority.Normal) =>
        _dispatcher.BeginInvoke(action, priority);
}

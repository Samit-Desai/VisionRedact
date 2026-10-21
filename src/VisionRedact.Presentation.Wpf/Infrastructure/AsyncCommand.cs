using System.Windows.Input;

namespace VisionRedact.Presentation.Wpf.Infrastructure;

/// <summary>ICommand over an async method: disabled while it runs, every exception goes to onError.</summary>
// why: Prism 8's DelegateCommand is sync. an async lambda in it silently becomes an async void, w/ no error
// handling and nothing stopping a double click.
// UI thread only (made, run and awaited there), so no locks
public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception> _onError;
    private bool _isExecuting;

    public AsyncCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _onError = onError;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsExecuting => _isExecuting;

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    // async void is ok only b/c ExecuteAsync hands every exception to onError
    public async void Execute(object? parameter) => await ExecuteAsync();

    // what Execute awaits. public so a caller could know when it's done, none outside does yet
    public async Task ExecuteAsync()
    {
        // a second click (or disabled): don't run it twice
        if (!CanExecute(null))
        {
            return;
        }

        // busy -> the button grays out while it runs
        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            // comes back on the UI thread (WPF's SynchronizationContext), so the flag stays single-threaded
            await _execute();
        }
        catch (Exception ex)
        {
            // usually: log + a friendly message
            _onError(ex);
        }
        finally
        {
            // always re-enable, even after an error or a cancel
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    // call it when what canExecute reads has changed (e.g. a video was opened)
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

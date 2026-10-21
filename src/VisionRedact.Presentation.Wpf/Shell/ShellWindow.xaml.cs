using System.ComponentModel;
using System.Windows;

namespace VisionRedact.Presentation.Wpf.Shell;

// the main window: header, Player region, Inspector region, status bar. App.CreateShell builds it.
// all logic is in ShellViewModel, this only forwards Loaded (start-up) and Closing (clean shutdown)
public partial class ShellWindow : Window
{
    private readonly ShellViewModel _viewModel;
    // set when cleanup is done, so the second Close() goes through
    private bool _shutdownComplete;

    public ShellWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // async void is ok at a real event edge (this, OnClosing and AsyncCommand.Execute are the only ones)
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // unhook first: start-up must run only once
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
    }

    // Closing is sync, and ShutdownAsync().Wait() here could deadlock as soon as an await needs this Dispatcher.
    // so: refuse the first close, await the cleanup (jobs, playback, decoder, models), then close again
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        // second pass, after cleanup: let it close
        if (_shutdownComplete)
        {
            return;
        }

        // first pass: refuse it, and disable the window so nothing gets clicked during cleanup
        e.Cancel = true;
        IsEnabled = false;
        try
        {
            await _viewModel.ShutdownAsync();
        }
        finally
        {
            _shutdownComplete = true;

            // posted, not called: if ShutdownAsync finished synchronously we're still inside Closing,
            // and Close() there throws InvalidOperationException
            _ = Dispatcher.BeginInvoke(Close);
        }
    }
}

using Prism.Commands;
using Prism.Mvvm;
using Prism.Services.Dialogs;

namespace VisionRedact.Presentation.Wpf.Features.Dialogs;

// one friendly dialog for errors and info, w/ optional technical details, instead of MessageBox calls all over the
// view models. open it through IDialogService w/ CreateParameters
public sealed class ErrorDialogViewModel : BindableBase, IDialogAware
{
    // shared by CreateParameters (writes) and OnDialogOpened (reads)
    private const string TitleKey = "title";
    private const string MessageKey = "message";
    private const string DetailsKey = "details";
    private const string IsErrorKey = "isError";

    private string _title = "Error";
    private string _message = string.Empty;
    private string? _details;
    private bool _isError = true;

    public ErrorDialogViewModel()
    {
        // RequestClose = Prism closes the window, result OK
        CloseCommand = new DelegateCommand(() => RequestClose?.Invoke(new DialogResult(ButtonResult.OK)));
    }

    public event Action<IDialogResult>? RequestClose;

    public DelegateCommand CloseCommand { get; }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public string? Details
    {
        get => _details;
        private set
        {
            // HasDetails shows the expander
            if (SetProperty(ref _details, value))
            {
                RaisePropertyChanged(nameof(HasDetails));
            }
        }
    }

    public bool HasDetails => !string.IsNullOrWhiteSpace(_details);

    public bool IsError
    {
        get => _isError;
        private set
        {
            if (SetProperty(ref _isError, value))
            {
                RaisePropertyChanged(nameof(Glyph));
            }
        }
    }

    // Segoe Fluent Icons: error badge / info
    public string Glyph => _isError ? "\uEA39" : "\uE946";

    /// <summary>The dialog text as Prism parameters. Null details hide the details section.</summary>
    public static IDialogParameters CreateParameters(string title, string message, string? details, bool isError = true) =>
        new DialogParameters { { TitleKey, title }, { MessageKey, message }, { DetailsKey, details ?? string.Empty }, { IsErrorKey, isError } };

    public bool CanCloseDialog() => true;

    public void OnDialogClosed()
    {
    }

    public void OnDialogOpened(IDialogParameters parameters)
    {
        // no isError key = an error
        Title = parameters.GetValue<string>(TitleKey) ?? "Error";
        Message = parameters.GetValue<string>(MessageKey) ?? string.Empty;
        Details = parameters.GetValue<string>(DetailsKey);
        IsError = !parameters.ContainsKey(IsErrorKey) || parameters.GetValue<bool>(IsErrorKey);
    }
}

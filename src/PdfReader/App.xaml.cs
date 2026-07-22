using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace PdfReader;

/// <summary>
/// Application entry point. Creates the main window and handles
/// file activation (the app being launched by double-clicking a .pdf).
/// </summary>
public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();

        // If we were launched by opening a .pdf file, load it right away.
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.File &&
            activation.Data is IFileActivatedEventArgs fileArgs &&
            fileArgs.Files.Count > 0 &&
            fileArgs.Files[0] is StorageFile file)
        {
            await Window.OpenFileAsync(file);
        }
    }
}

using Microsoft.UI.Xaml;

namespace Nami;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();

        // "Nami.exe <file>" opens the file straight away.
        string[] argv = Environment.GetCommandLineArgs();
        if (argv.Length > 1)
            Window.Page.Open(argv[1]);
    }
}

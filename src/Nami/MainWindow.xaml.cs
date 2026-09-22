using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Nami;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1280, 720 + 32));

        if (AppWindow.TitleBar is { } tb)
        {
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor = Colors.White;
            tb.ButtonInactiveForegroundColor = Colors.Gray;
        }
    }

    public MainPage Page => Main;

    public bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    public void ToggleFullScreen()
    {
        if (IsFullScreen) ExitFullScreen();
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            TitleBarDragRegion.Visibility = Visibility.Collapsed;
        }
    }

    public void ExitFullScreen()
    {
        if (!IsFullScreen) return;
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        TitleBarDragRegion.Visibility = Visibility.Visible;
    }
}

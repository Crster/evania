using System;
using Evania.ViewModels;
using Evania.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Evania.Windows;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        Root.DataContext = ViewModel;

        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.Resize(new SizeInt32(1024, 768));

        DisplayArea displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);

        int x = displayArea.WorkArea.X + (displayArea.WorkArea.Width - AppWindow.Size.Width) / 2;
        int y = displayArea.WorkArea.Y + (displayArea.WorkArea.Height - AppWindow.Size.Height) / 2;

        AppWindow.Move(new PointInt32(x, y));
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = ViewModel.SelectedMenuItem?.Tag ?? string.Empty;
        Type? pageType = tag switch
        {
            nameof(ScreenshotPage) => typeof(ScreenshotPage),
            _ => null
        };

        if (pageType != null &&
            NavigationPresenter.CurrentSourcePageType != pageType)
        {
            NavigationPresenter.Navigate(pageType);
        }
    }
}

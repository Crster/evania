using Evania.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Evania.Views;

public sealed partial class ScreenRecordPage : Page
{
    public ScreenRecordPageViewModel ViewModel { get; } = new();

    public ScreenRecordPage()
    {
        InitializeComponent();
        DataContext = ViewModel;

        // Assigned before the element loads so the transport controls drive our player.
        Player.SetMediaPlayer(ViewModel.Player);
    }

    private async void ScreenRecordPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadRecordingsCommand.ExecuteAsync(null);

        if (App.MainWindow is Evania.Windows.MainWindow mainWindow)
        {
            mainWindow.TitleBarSearchChanged += MainWindow_TitleBarSearchChanged;
            ViewModel.Filter = mainWindow.TitleBarSearchText;
        }
    }

    private void MainWindow_TitleBarSearchChanged(object? sender, string text) => ViewModel.Filter = text;

    private void ScreenRecordPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is Evania.Windows.MainWindow mainWindow)
        {
            mainWindow.TitleBarSearchChanged -= MainWindow_TitleBarSearchChanged;
        }
    }

    private void ErrorBar_Closed(InfoBar sender, object args)
    {
        ViewModel.ClearErrorCommand.Execute(null);
    }
}

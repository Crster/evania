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
    }

    private void ErrorBar_Closed(InfoBar sender, object args)
    {
        ViewModel.ClearErrorCommand.Execute(null);
    }
}

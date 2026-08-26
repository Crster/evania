using Evania.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Evania.Views;

public sealed partial class ScreenshotPage : Page
{
    public ScreenshotPageViewModel ViewModel { get; } = new();

    public ScreenshotPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }

    private async void ScreenshotPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadScreenshotsCommand.ExecuteAsync(null);
    }

    private void Grid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScreenshotItem item })
        {
            item.IsPointerOver = true;
        }
    }

    private void Grid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScreenshotItem item })
        {
            item.IsPointerOver = false;
        }
    }
}
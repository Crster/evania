using System;
using System.Linq;
using System.Runtime.InteropServices;
using Evania.Services;
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

    /// <summary>
    /// Set when the app is genuinely on its way out - the tray's Exit entry - so that closing the
    /// window is allowed to mean what it usually means instead of hiding it again.
    /// </summary>
    public bool IsQuitting { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        Root.DataContext = ViewModel;

        WindowIconService.ApplyTo(this);

        ExtendsContentIntoTitleBar = true;

        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.Resize(new SizeInt32(1024, 768));

        DisplayArea displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);

        int x = displayArea.WorkArea.X + (displayArea.WorkArea.Width - AppWindow.Size.Width) / 2;
        int y = displayArea.WorkArea.Y + (displayArea.WorkArea.Height - AppWindow.Size.Height) / 2;

        AppWindow.Move(new PointInt32(x, y));

        AppWindow.Closing += AppWindow_Closing;

        // The first page used to arrive through the selection binding; now the pane is the single
        // place that knows what is selected, so the opening item has to be said out loud.
        NavView.SelectedItem = ViewModel.MenuItems[0];
    }

    /// <summary>
    /// The title bar search is the window's own; when a page is a NotePage, its view model
    /// subscribes to this so the filter stays live.
    /// </summary>
    public event EventHandler<string>? TitleBarSearchChanged;

    public string TitleBarSearchText => TitleBarSearchBox.Text;

    private void TitleBarSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        TitleBarSearchChanged?.Invoke(this, sender.Text);
    }

    /// <summary>
    /// Brings the window back for the user to work in. Called from the tray after the window has
    /// been hidden - or never shown at all, for a session that started straight into the tray.
    /// </summary>
    public void Present()
    {
        AppWindow.Show();

        if (AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        Activate();

        // Showing a window is not itself a gesture the shell treats as a request for the foreground,
        // so a tray activation can come back underneath whatever was focused before it.
        NativeMethods.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    /// <summary>
    /// Shows Settings from somewhere other than the gear - the tray menu, mainly. Selecting the
    /// settings item raises the same event a click would and lights the entry up, but a window that
    /// started life hidden in the tray has no realised pane to ask, so navigating is what carries the
    /// request and the selection is only the polish on it.
    /// </summary>
    public void NavigateToSettings()
    {
        Navigate(typeof(SettingsPage));

        if (NavView.SettingsItem is { } settingsItem)
        {
            NavView.SelectedItem = settingsItem;
        }
    }

    /// <summary>
    /// Shows the Chat page - what a Copilot key press means. The chat entry is an ordinary menu
    /// item, so lighting it up in the pane and navigating go together the way they do for the
    /// gear above.
    /// </summary>
    public void NavigateToChat()
    {
        Navigate(typeof(ChatPage));

        if (ViewModel.MenuItems.FirstOrDefault(i => i.Tag == nameof(ChatPage)) is { } chatItem)
        {
            NavView.SelectedItem = chatItem;
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (SettingsService.Current.CloseToTray && !IsQuitting)
        {
            // The process has to survive this, and it does: hiding keeps the window alive, which is
            // what keeps the dispatcher queue - and with it the tray icon and the shortcut - running.
            args.Cancel = true;
            sender.Hide();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // The footer gear arrives through this event too, flagged rather than as a menu item.
        if (args.IsSettingsSelected)
        {
            TitleBarSearchBox.PlaceholderText = "Search settings";
            Navigate(typeof(SettingsPage));

            return;
        }

        // The item comes from the event: the settings entry is not one of the view model's items,
        // and this is the one place that has to tell the two apart.
        NavigationItemViewModel? item = args.SelectedItem as NavigationItemViewModel;

        Type? pageType = item?.Tag switch
        {
            nameof(ChatPage) => typeof(ChatPage),
            nameof(ScreenshotPage) => typeof(ScreenshotPage),
            nameof(ScreenRecordPage) => typeof(ScreenRecordPage),
            nameof(NotePage) => typeof(NotePage),
            _ => null
        };

        if (pageType != null)
        {
            TitleBarSearchBox.PlaceholderText = item!.Content switch
            {
                "Screenshot" => "Search screenshots",
                "Screen Record" => "Search recordings",
                "Notes" => "Search notes",
                _ => "Search"
            };
            Navigate(pageType);
        }
    }

    private void Navigate(Type pageType)
    {
        if (NavigationPresenter.CurrentSourcePageType != pageType)
        {
            NavigationPresenter.Navigate(pageType);
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}

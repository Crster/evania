using System;
using System.ComponentModel;
using Evania.Services;
using Evania.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Evania.Views;

/// <summary>
/// The Chat page: nothing but a browser aimed at whichever AI provider the settings choose. The
/// browser's profile folder is fixed by <see cref="SettingsService.ChatDataFolder"/>, which is
/// what keeps a provider's sign-in alive between sessions.
/// </summary>
public sealed partial class ChatPage : Page
{
    public ChatPageViewModel ViewModel { get; } = new();

    /// <summary>
    /// The WebView2 initialises once per page instance. Source is set only after the environment
    /// is ready - a Source in XAML would let the control initialise itself against the default
    /// profile folder before this code could steer it to the shared one.
    /// </summary>
    private bool initializing;
    private bool coreEventsAttached;
    private bool canGoBack;

    public event EventHandler? BrowserNavigationStateChanged;

    public bool CanGoBackInBrowser => ChatBrowser.CanGoBack;

    public ChatPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }

    private async void ChatPage_Loaded(object sender, RoutedEventArgs e)
    {
        SettingsService.Current.PropertyChanged -= Settings_PropertyChanged;
        SettingsService.Current.PropertyChanged += Settings_PropertyChanged;

        if (ChatBrowser.CoreWebView2 is not null)
        {
            AttachCoreWebView2Events();
            SyncChatSource();
            UpdateNavigationState();
            return;
        }

        if (initializing)
        {
            return;
        }

        initializing = true;

        CoreWebView2Environment environment = await SettingsService.GetChatWebViewEnvironmentAsync();

        await ChatBrowser.EnsureCoreWebView2Async(environment);
        AttachCoreWebView2Events();

        // Match the window's backdrop so the content arriving does not flash white (or black)
        // against it first.
        ChatBrowser.DefaultBackgroundColor = ActualTheme == ElementTheme.Dark
            ? global::Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : global::Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);

        SyncChatSource();
        UpdateNavigationState();
    }

    private void ChatPage_Unloaded(object sender, RoutedEventArgs e)
    {
        SettingsService.Current.PropertyChanged -= Settings_PropertyChanged;
        DetachCoreWebView2Events();
    }

    /// <summary>
    /// A provider switch made in Settings moves a loaded Chat page at once - the setting applies
    /// as it is flipped, like every other switch in the app.
    /// </summary>
    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ChatProvider) && ChatBrowser.CoreWebView2 is not null)
        {
            SyncChatSource();
        }
    }

    private void SyncChatSource()
    {
        Uri target = ViewModel.ChatUrl;

        if (ChatBrowser.Source != target)
        {
            ChatBrowser.Source = target;
        }
    }

    public bool TryGoBackInBrowser()
    {
        if (!ChatBrowser.CanGoBack)
        {
            return false;
        }

        ChatBrowser.GoBack();
        return true;
    }

    private void AttachCoreWebView2Events()
    {
        if (coreEventsAttached || ChatBrowser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        core.HistoryChanged += CoreWebView2_HistoryChanged;
        core.NavigationCompleted += CoreWebView2_NavigationCompleted;
        coreEventsAttached = true;
    }

    private void DetachCoreWebView2Events()
    {
        if (!coreEventsAttached || ChatBrowser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        core.HistoryChanged -= CoreWebView2_HistoryChanged;
        core.NavigationCompleted -= CoreWebView2_NavigationCompleted;
        coreEventsAttached = false;
    }

    private void CoreWebView2_HistoryChanged(object? sender, object e)
    {
        UpdateNavigationState();
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        UpdateNavigationState();
    }

    private void UpdateNavigationState()
    {
        bool nextCanGoBack = ChatBrowser.CanGoBack;

        if (nextCanGoBack == canGoBack)
        {
            return;
        }

        canGoBack = nextCanGoBack;
        BrowserNavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }
}

using System;
using Evania.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;

namespace Evania.Views;

/// <summary>
/// Displays a saved webpage bookmark in the app shell.
/// </summary>
public sealed partial class BookmarkPage : Page
{
    private bool initializing;
    private Uri? pendingUri;

    public BookmarkPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is string url &&
            Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            pendingUri = uri;
            NavigateToPendingUri();
        }
    }

    private async void BookmarkPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (initializing || BookmarkBrowser.CoreWebView2 is not null)
        {
            NavigateToPendingUri();
            return;
        }

        initializing = true;

        CoreWebView2Environment environment = await SettingsService.GetChatWebViewEnvironmentAsync();

        await BookmarkBrowser.EnsureCoreWebView2Async(environment);

        BookmarkBrowser.DefaultBackgroundColor = ActualTheme == ElementTheme.Dark
            ? global::Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : global::Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);

        NavigateToPendingUri();
    }

    private void NavigateToPendingUri()
    {
        if (pendingUri is not null && BookmarkBrowser.CoreWebView2 is not null)
        {
            BookmarkBrowser.Source = pendingUri;
        }
    }
}

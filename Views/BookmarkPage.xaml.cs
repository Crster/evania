using System;
using System.Text.Json;
using System.Threading.Tasks;
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
    private bool pageReady;
    private Uri? pendingUri;
    private string pendingSearchText = string.Empty;

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
        if (App.MainWindow is Evania.Windows.MainWindow mainWindow)
        {
            mainWindow.TitleBarSearchChanged += MainWindow_TitleBarSearchChanged;
            pendingSearchText = mainWindow.TitleBarSearchText;
        }

        BookmarkBrowser.NavigationCompleted += BookmarkBrowser_NavigationCompleted;

        if (initializing || BookmarkBrowser.CoreWebView2 is not null)
        {
            NavigateToPendingUri();
            if (BookmarkBrowser.CoreWebView2 is not null && pendingUri is null)
            {
                pageReady = true;
                await ApplySearchAsync(pendingSearchText);
            }

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

    private void BookmarkPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is Evania.Windows.MainWindow mainWindow)
        {
            mainWindow.TitleBarSearchChanged -= MainWindow_TitleBarSearchChanged;
        }

        BookmarkBrowser.NavigationCompleted -= BookmarkBrowser_NavigationCompleted;
    }

    private void MainWindow_TitleBarSearchChanged(object? sender, string text)
    {
        pendingSearchText = text;

        if (pageReady)
        {
            _ = ApplySearchAsync(text);
        }
    }

    private void BookmarkBrowser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        pageReady = e.IsSuccess;

        if (pageReady)
        {
            _ = ApplySearchAsync(pendingSearchText);
        }
    }

    private async Task ApplySearchAsync(string? searchText = null)
    {
        if (BookmarkBrowser.CoreWebView2 is null || !pageReady)
        {
            return;
        }

        string[] terms = SearchService.SplitTerms(searchText ?? pendingSearchText);
        string script = BuildHighlightScript(terms);

        await BookmarkBrowser.CoreWebView2.ExecuteScriptAsync(script);
    }

    private static string BuildHighlightScript(string[] terms)
    {
        string termsJson = JsonSerializer.Serialize(terms);

        return $$"""
(() => {
    const terms = {{termsJson}};
    const activeTerms = terms.map(term => term.trim().toLowerCase()).filter(term => term.length > 0);

    document.querySelectorAll('mark[data-evania-search-highlight="true"]').forEach(mark => {
        const parent = mark.parentNode;
        const text = document.createTextNode(mark.textContent ?? '');
        mark.replaceWith(text);
        parent?.normalize();
    });

    if (activeTerms.length === 0 || !document.body) {
        return;
    }

    const matchesTerm = token => activeTerms.some(term => token.toLowerCase().startsWith(term));
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
        acceptNode(node) {
            const parent = node.parentElement;
            if (!parent || parent.closest('script, style, noscript, mark')) {
                return NodeFilter.FILTER_REJECT;
            }

            return node.nodeValue && node.nodeValue.trim().length > 0
                ? NodeFilter.FILTER_ACCEPT
                : NodeFilter.FILTER_REJECT;
        }
    });

    const nodes = [];
    while (walker.nextNode()) {
        nodes.push(walker.currentNode);
    }

    let firstHighlight = null;

    for (const node of nodes) {
        const parts = node.nodeValue.split(/(\s+)/);
        if (!parts.some(part => part && !/^\s+$/.test(part) && matchesTerm(part))) {
            continue;
        }

        const fragment = document.createDocumentFragment();
        for (const part of parts) {
            if (!part) {
                continue;
            }

            if (/^\s+$/.test(part) || !matchesTerm(part)) {
                fragment.appendChild(document.createTextNode(part));
                continue;
            }

            const mark = document.createElement('mark');
            mark.setAttribute('data-evania-search-highlight', 'true');
            mark.textContent = part;
            firstHighlight ??= mark;
            fragment.appendChild(mark);
        }

        node.parentNode?.replaceChild(fragment, node);
    }

    firstHighlight?.scrollIntoView({ block: 'center' });
})();
""";
    }

    private void NavigateToPendingUri()
    {
        if (pendingUri is not null && BookmarkBrowser.CoreWebView2 is not null)
        {
            pageReady = false;
            BookmarkBrowser.Source = pendingUri;
        }
    }
}

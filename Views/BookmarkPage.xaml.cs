using System;
using System.Text.Json;
using System.Threading.Tasks;
using Evania.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Evania.Views;

/// <summary>
/// Displays a saved webpage bookmark in the app shell.
/// </summary>
public sealed partial class BookmarkPage : Page
{
    private bool initializing;
    private bool pageReady;
    private bool coreEventsAttached;
    private bool canGoBack;
    private Uri? pendingUri;
    private Uri? currentUri;
    private string pendingSearchText = string.Empty;
    private string currentDocumentTitle = string.Empty;
    private bool isDocumentPlayingAudio;

    public event EventHandler? TrayMediaStateChanged;
    public event EventHandler? BrowserNavigationStateChanged;

    public bool CanGoBackInBrowser => BookmarkBrowser.CanGoBack;

    public string? ActiveMediaTitle => isDocumentPlayingAudio
        ? NormalizeTitle(currentDocumentTitle)
        : null;

    public BookmarkPage()
    {
        InitializeComponent();
    }

    public void Navigate(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            pendingUri = uri;
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
        AttachCoreWebView2Events();

        if (initializing || BookmarkBrowser.CoreWebView2 is not null)
        {
            if (pendingUri == currentUri && pageReady)
            {
                pendingUri = null;
                return;
            }

            NavigateToPendingUri();
            if (BookmarkBrowser.CoreWebView2 is not null && pendingUri is null)
            {
                pageReady = true;
                UpdateMediaStateFromBrowser();
                UpdateNavigationState();
                await ApplySearchAsync(pendingSearchText);
            }

            return;
        }

        initializing = true;

        CoreWebView2Environment environment = await SettingsService.GetChatWebViewEnvironmentAsync();

        await BookmarkBrowser.EnsureCoreWebView2Async(environment);
        AttachCoreWebView2Events();

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
        DetachCoreWebView2Events();
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
            currentUri = pendingUri;
            UpdateMediaStateFromBrowser();
            UpdateNavigationState();
            _ = ApplySearchAsync(pendingSearchText);
        }
        else
        {
            ResetMediaState();
            UpdateNavigationState();
        }
    }

    private void CoreWebView2_DocumentTitleChanged(object? sender, object e)
    {
        UpdateMediaStateFromBrowser();
    }

    private void CoreWebView2_IsDocumentPlayingAudioChanged(object? sender, object e)
    {
        UpdateMediaStateFromBrowser();
    }

    private void CoreWebView2_HistoryChanged(object? sender, object e)
    {
        UpdateNavigationState();
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
            ResetMediaState();
            UpdateNavigationState();
            BookmarkBrowser.Source = pendingUri;
        }
    }

    private void AttachCoreWebView2Events()
    {
        if (coreEventsAttached || BookmarkBrowser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        core.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;
        core.IsDocumentPlayingAudioChanged += CoreWebView2_IsDocumentPlayingAudioChanged;
        core.HistoryChanged += CoreWebView2_HistoryChanged;
        coreEventsAttached = true;
        UpdateMediaStateFromBrowser();
        UpdateNavigationState();
    }

    private void DetachCoreWebView2Events()
    {
        if (!coreEventsAttached || BookmarkBrowser.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        core.DocumentTitleChanged -= CoreWebView2_DocumentTitleChanged;
        core.IsDocumentPlayingAudioChanged -= CoreWebView2_IsDocumentPlayingAudioChanged;
        core.HistoryChanged -= CoreWebView2_HistoryChanged;
        coreEventsAttached = false;
    }

    private void UpdateMediaStateFromBrowser()
    {
        if (BookmarkBrowser.CoreWebView2 is not CoreWebView2 core)
        {
            ResetMediaState();
            return;
        }

        string newTitle = core.DocumentTitle ?? string.Empty;
        bool newIsDocumentPlayingAudio = core.IsDocumentPlayingAudio;

        if (newTitle == currentDocumentTitle && newIsDocumentPlayingAudio == isDocumentPlayingAudio)
        {
            return;
        }

        currentDocumentTitle = newTitle;
        isDocumentPlayingAudio = newIsDocumentPlayingAudio;
        TrayMediaStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetMediaState()
    {
        if (currentDocumentTitle.Length == 0 && !isDocumentPlayingAudio)
        {
            return;
        }

        currentDocumentTitle = string.Empty;
        isDocumentPlayingAudio = false;
        TrayMediaStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool TryGoBackInBrowser()
    {
        if (!BookmarkBrowser.CanGoBack)
        {
            return false;
        }

        BookmarkBrowser.GoBack();
        return true;
    }

    private void UpdateNavigationState()
    {
        bool nextCanGoBack = BookmarkBrowser.CanGoBack;

        if (nextCanGoBack == canGoBack)
        {
            return;
        }

        canGoBack = nextCanGoBack;
        BrowserNavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string? NormalizeTitle(string? title)
    {
        string normalized = title?.Trim() ?? string.Empty;

        return normalized.Length == 0
            ? null
            : normalized;
    }
}

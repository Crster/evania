using System;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private bool mediaWatcherInstalled;

    // What the page itself says is playing, straight from the Media Session API - the same metadata
    // Windows shows in its own media flyout. Reported by the injected watcher below.
    private bool hasMediaSessionReport;
    private bool mediaSessionPlaying;
    private string mediaSessionTitle = string.Empty;

    public event EventHandler? TrayMediaStateChanged;
    public event EventHandler? BrowserNavigationStateChanged;

    public bool CanGoBackInBrowser => BookmarkBrowser.CanGoBack;

    /// <summary>
    /// What the tray tooltip shows while this page is playing something.
    ///
    /// The page's own media session is asked first. The document title is only a stand-in for it:
    /// a streaming site rewrites the tab title well after the track has changed - and sometimes not
    /// at all - which is what made the tooltip name the previous song. The media session carries the
    /// real track, and changes the instant it does.
    /// </summary>
    public string? ActiveMediaTitle
    {
        get
        {
            if (hasMediaSessionReport)
            {
                return mediaSessionPlaying
                    ? NormalizeTitle(mediaSessionTitle) ?? NormalizeTitle(currentDocumentTitle)
                    : null;
            }

            return isDocumentPlayingAudio
                ? NormalizeTitle(currentDocumentTitle)
                : null;
        }
    }

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

        // The CoreWebView2 handlers stay attached on purpose. Navigating to another tab unloads this
        // page but does not stop its audio, and the tray tooltip names whichever bookmark is
        // playing - which is usually one the user has navigated away from.
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

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;

        try
        {
            json = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            // Not a string message - something else on the page is talking to the host.
            return;
        }

        MediaReport? report;

        try
        {
            report = JsonSerializer.Deserialize(json, MediaJsonContext.Default.MediaReport);
        }
        catch (JsonException)
        {
            return;
        }

        if (report?.Source != MediaReportSource)
        {
            return;
        }

        string title = (report.Title ?? string.Empty).Trim();

        if (hasMediaSessionReport &&
            report.Playing == mediaSessionPlaying &&
            title == mediaSessionTitle)
        {
            return;
        }

        hasMediaSessionReport = true;
        mediaSessionPlaying = report.Playing;
        mediaSessionTitle = title;
        TrayMediaStateChanged?.Invoke(this, EventArgs.Empty);
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
        core.WebMessageReceived += CoreWebView2_WebMessageReceived;
        coreEventsAttached = true;
        _ = InstallMediaWatcherAsync(core);
        UpdateMediaStateFromBrowser();
        UpdateNavigationState();
    }

    /// <summary>
    /// Registers the media watcher for every document this browser loads, and runs it once against
    /// the document already showing so an existing page starts reporting without a reload.
    /// </summary>
    private async Task InstallMediaWatcherAsync(CoreWebView2 core)
    {
        if (mediaWatcherInstalled)
        {
            return;
        }

        mediaWatcherInstalled = true;

        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(MediaWatcherScript);
            await core.ExecuteScriptAsync(MediaWatcherScript);
        }
        catch (Exception)
        {
            // The watcher is an improvement on the document-title fallback, not a requirement for
            // it; a browser that refuses the script still gets a tooltip.
            mediaWatcherInstalled = false;
        }
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
        if (currentDocumentTitle.Length == 0 && !isDocumentPlayingAudio && !hasMediaSessionReport)
        {
            return;
        }

        currentDocumentTitle = string.Empty;
        isDocumentPlayingAudio = false;

        // The next document installs its own watcher; until it reports, this page has no media
        // session to speak for and falls back to the document title.
        hasMediaSessionReport = false;
        mediaSessionPlaying = false;
        mediaSessionTitle = string.Empty;

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

    private const string MediaReportSource = "evania-media";

    /// <summary>
    /// Watches what the page is actually playing and posts it to the host.
    ///
    /// Three things make this beat <c>IsDocumentPlayingAudio</c> plus the document title, which is
    /// what the tooltip used before:
    /// <list type="bullet">
    /// <item>the Media Session metadata is the track, not the tab caption, so a site that leaves its
    /// title alone between songs - or rewrites it seconds later - no longer names the wrong one;</item>
    /// <item><c>play</c> and <c>pause</c> are listened for in the capture phase, so a start or stop
    /// is reported at once instead of after the host's audio-state debounce;</item>
    /// <item>the poll covers metadata changes, which have no event of their own - the track can
    /// change without any element firing anything.</item>
    /// </list>
    /// Only the top frame reports: an embedded player's frame has no <c>chrome.webview</c> to post
    /// through, and its title is not the one the user is looking at.
    /// </summary>
    private const string MediaWatcherScript = """
(() => {
    if (window.top !== window || window.__evaniaMediaWatcher) {
        return;
    }

    const host = window.chrome && window.chrome.webview;
    if (!host || typeof host.postMessage !== 'function') {
        return;
    }

    window.__evaniaMediaWatcher = true;
    let lastPayload = '';

    const isActiveElement = element =>
        !element.paused && !element.ended && element.readyState > 2 && element.currentTime > 0;

    const report = () => {
        const session = navigator.mediaSession;
        const metadata = session && session.metadata;
        const state = session && session.playbackState;

        let playing;
        if (state === 'playing') {
            playing = true;
        } else if (state === 'paused') {
            playing = false;
        } else {
            playing = Array.prototype.some.call(
                document.querySelectorAll('video, audio'),
                isActiveElement);
        }

        const payload = JSON.stringify({
            source: 'evania-media',
            playing: playing,
            title: (metadata && metadata.title) || ''
        });

        if (payload === lastPayload) {
            return;
        }

        lastPayload = payload;
        host.postMessage(payload);
    };

    for (const name of ['play', 'pause', 'ended', 'emptied', 'loadedmetadata']) {
        document.addEventListener(name, report, true);
    }

    setInterval(report, 400);
    report();
})();
""";

    /// <summary>The shape the watcher posts. Kept flat so the payload comparison is a string compare.</summary>
    private sealed class MediaReport
    {
        public string? Source { get; set; }

        public bool Playing { get; set; }

        public string? Title { get; set; }
    }

    // Source-generated: Release trims, and reflection-based deserialization would warn.
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(MediaReport))]
    private partial class MediaJsonContext : JsonSerializerContext
    {
    }

    private static string? NormalizeTitle(string? title)
    {
        string normalized = title?.Trim() ?? string.Empty;

        return normalized.Length == 0
            ? null
            : normalized;
    }
}

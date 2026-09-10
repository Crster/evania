using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evania.Services;
using Microsoft.UI.Dispatching;

namespace Evania.ViewModels
{
    /// <summary>One entry in the shortcut dropdown.</summary>
    public sealed record HotkeyOption(ScreenshotHotkey Value, string Display);

    /// <summary>One entry in the chat provider dropdown.</summary>
    public sealed record ChatProviderOption(ChatProvider Value, string Display);

    /// <summary>
    /// Backs the Settings page.
    ///
    /// The two kinds of setting are applied differently on purpose. The General and Screenshot
    /// switches take effect the moment they move, because each one has an immediate, observable
    /// answer from the operating system - a startup task state, a registered hotkey, a saved file.
    /// The Database pair waits for Apply: a path and an encryption switch describe one file, and
    /// applying half of that pair - moving the path while the key still matches the old file, say -
    /// is exactly the mistake the Apply button exists to prevent.
    /// </summary>
    public partial class SettingsPageViewModel : ObservableObject
    {
        /// <summary>The live settings instance the page reads through.</summary>
        public AppSettings Settings { get; } = SettingsService.Current;

        public IReadOnlyList<HotkeyOption> HotkeyOptions { get; } =
        [
            new HotkeyOption(ScreenshotHotkey.None, ScreenshotHotkey.None.ToDisplayString()),
            new HotkeyOption(ScreenshotHotkey.PrintScreen, ScreenshotHotkey.PrintScreen.ToDisplayString()),
            new HotkeyOption(ScreenshotHotkey.AltPrintScreen, ScreenshotHotkey.AltPrintScreen.ToDisplayString()),
            new HotkeyOption(ScreenshotHotkey.CtrlShift3, ScreenshotHotkey.CtrlShift3.ToDisplayString()),
            new HotkeyOption(ScreenshotHotkey.AltP, ScreenshotHotkey.AltP.ToDisplayString()),
        ];

        public IReadOnlyList<ChatProviderOption> ChatProviderOptions { get; } =
        [
            new ChatProviderOption(ChatProvider.Copilot, ChatProvider.Copilot.ToDisplayString()),
            new ChatProviderOption(ChatProvider.Gemini, ChatProvider.Gemini.ToDisplayString()),
            new ChatProviderOption(ChatProvider.ChatGPT, ChatProvider.ChatGPT.ToDisplayString()),
            new ChatProviderOption(ChatProvider.Claude, ChatProvider.Claude.ToDisplayString()),
        ];

        private static readonly HttpClient BookmarkTitleHttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        private const string BookmarkTitleUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36 Edg/138.0.0.0";

        private static readonly string[] InvalidBookmarkTitleFragments =
        [
            "outdated browser",
            "unsupported browser",
            "browser not supported",
            "your browser is deprecated",
            "please upgrade"
        ];

        private static readonly Regex HtmlTitleRegex = new("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        public ObservableCollection<WebBookmark> Bookmarks { get; } = [];

        private string newBookmarkUrl = string.Empty;

        public string NewBookmarkUrl
        {
            get => newBookmarkUrl;
            set
            {
                if (SetProperty(ref newBookmarkUrl, value))
                {
                    OnPropertyChanged(nameof(CanAddBookmark));
                }
            }
        }

        private bool isAddingBookmark;

        public bool IsAddingBookmark
        {
            get => isAddingBookmark;
            private set
            {
                if (SetProperty(ref isAddingBookmark, value))
                {
                    OnPropertyChanged(nameof(CanAddBookmark));
                }
            }
        }

        public bool CanAddBookmark => !IsAddingBookmark && !string.IsNullOrWhiteSpace(NewBookmarkUrl);

        public bool HasBookmarks => Bookmarks.Count > 0;

        /// <summary>The path typed into the box, which may differ from the live one until Apply.</summary>
        [ObservableProperty]
        public partial string CandidatePath { get; set; }

        /// <summary>The password typed into the box, likewise staged until Apply.</summary>
        [ObservableProperty]
        public partial string CandidatePassword { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ApplyIsEnabled), nameof(ShowApplyLabel))]
        public partial bool IsApplying { get; set; }

        /// <summary>Briefly true after a successful apply so the button can show a check before resetting.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ApplyIsEnabled), nameof(ShowApplyLabel))]
        public partial bool IsSuccess { get; set; }

        [ObservableProperty]
        public partial string StatusText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ErrorMessage { get; set; } = string.Empty;

        /// <summary>
        /// Says up front that the file at the staged path is encrypted, so selecting a protected
        /// database is not silent until Apply fails. Empty when the path is not an encrypted file.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasEncryptionNotice))]
        public partial string EncryptionNotice { get; set; } = string.Empty;

        public bool HasEncryptionNotice => EncryptionNotice.Length > 0;

        /// <summary>
        /// Optional UI callback the page supplies to request a password when an existing protected
        /// database is selected without one. The bool says whether the previous password was
        /// rejected, so the prompt can say so rather than repeating its opening question.
        /// </summary>
        public Func<string, bool, Task<string?>>? RequestDatabasePasswordAsync { get; set; }

        /// <summary>The dropdown selection, as the option record the ComboBox actually holds.</summary>
        public HotkeyOption SelectedHotkeyOption
        {
            get => field ?? HotkeyOptions[0];
            set
            {
                if (SetProperty(ref field, value) && value is not null && !stagingHotkey)
                {
                    SetShortcut(value.Value);
                }
            }
        } = null!;

        /// <summary>The chat provider dropdown selection, as the option record the ComboBox holds.</summary>
        public ChatProviderOption SelectedChatProviderOption
        {
            get => field ?? ChatProviderOptions[0];
            set => SetProperty(ref field, value);
        } = null!;

        private bool stagingHotkey;

        // Applying a different database reloads the bookmarks from it on a background thread, so
        // the list this page binds has to be rebuilt back on the UI thread.
        private readonly DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        public SettingsPageViewModel()
        {
            ResetStagingFromSettings();

            // Both General switches feed one sentence, and either can move from somewhere other than
            // this page - the tray menu, a corrected startup state - so the note follows the settings
            // rather than the switch that happened to be clicked.
            Settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AppSettings.CloseToTray) or nameof(AppSettings.StartWithWindows))
                {
                    OnPropertyChanged(nameof(StartsHiddenInTray));
                }

                if (e.PropertyName != nameof(AppSettings.Bookmarks))
                {
                    return;
                }

                if (dispatcherQueue is null || dispatcherQueue.HasThreadAccess)
                {
                    SyncBookmarksFromSettings();
                }
                else
                {
                    dispatcherQueue.TryEnqueue(SyncBookmarksFromSettings);
                }
            };

            RefreshStatus();
        }

        /// <summary>
        /// Call every time the Settings page appears. It keeps the staged path, password and dropdown
        /// aligned with the persisted/live settings so reopening the page does not show stale values.
        /// </summary>
        public void ResetStagingFromSettings()
        {
            // Restoring the values the page already reflects is not the user picking a database, so
            // it must not trigger the password prompt.
            resettingStaging = true;

            try
            {
                CandidatePath = Settings.DatabasePath;
                CandidatePassword = DatabaseService.CurrentPassword ?? Settings.DatabasePassword ?? string.Empty;
            }
            finally
            {
                resettingStaging = false;
            }

            lastPromptedPath = null;

            // Staged quietly: assigning the record the binding already shows must not read as a
            // request to re-register the hotkey.
            stagingHotkey = true;
            SelectedHotkeyOption = HotkeyOptions.FirstOrDefault(o => o.Value == Settings.ScreenshotShortcut) ?? HotkeyOptions[0];
            stagingHotkey = false;

            SelectedChatProviderOption = ChatProviderOptions.FirstOrDefault(o => o.Value == Settings.ChatProvider) ?? ChatProviderOptions[0];
            UpdateEncryptionNotice();
            SyncBookmarksFromSettings();
            NewBookmarkUrl = string.Empty;
        }

        public bool HasError => ErrorMessage.Length > 0;

        /// <summary>Apply covers the path and the password, which are staged until it is clicked.</summary>
        public bool NeedsApply =>
            !string.Equals(CandidatePath.Trim(), Settings.DatabasePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(CandidatePassword, DatabaseService.CurrentPassword ?? Settings.DatabasePassword ?? string.Empty, StringComparison.Ordinal);

        /// <summary>
        /// The Apply button stays usable while something is staged, goes inert while the work runs,
        /// and turns back on for the brief success check so the flash does not render as a disabled
        /// button.
        /// </summary>
        public bool ApplyIsEnabled => (NeedsApply || IsSuccess) && !IsApplying;

        /// <summary>The plain label only shows when neither the spinner nor the check is up.</summary>
        public bool ShowApplyLabel => !IsApplying && !IsSuccess;

        /// <summary>
        /// True when a launch will land in the tray with no window - which only happens with both
        /// General switches on, and is worth saying where it comes from.
        /// </summary>
        public bool StartsHiddenInTray => Settings.CloseToTray && Settings.StartWithWindows;

        /// <summary>Where auto-save writes, named so the setting is not a mystery.</summary>
        public string AutoSaveFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

        public string AutoSaveSubtitle => $"Writes the screenshot you end up with straight to {AutoSaveFolder}.";

        partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

        partial void OnCandidatePathChanged(string value)
        {
            OnPropertyChanged(nameof(NeedsApply));
            OnPropertyChanged(nameof(ApplyIsEnabled));
            UpdateEncryptionNotice();

            // Picking a protected database asks for its password there and then, rather than
            // waiting for Apply to be pressed. Nothing is opened by this - the answer is staged
            // alongside the path, and the pair is still applied together.
            _ = PromptForStagedPathPasswordAsync(value);
        }

        /// <summary>
        /// True while <see cref="ResetStagingFromSettings"/> is putting the persisted values back,
        /// so that reopening the page cannot read as a database selection.
        /// </summary>
        private bool resettingStaging;

        /// <summary>The path the prompt last opened for, so a re-entry cannot stack dialogs.</summary>
        private string? lastPromptedPath;

        private bool isPromptingForPath;

        /// <summary>
        /// Asks for the password of a newly staged encrypted database.
        ///
        /// The prompt is unconditional for a file that is not the one already open: a password
        /// saved in Windows Credential Manager says this machine opened the file once, not that
        /// whoever is at the keyboard now may open it again.
        /// </summary>
        private async Task PromptForStagedPathPasswordAsync(string path)
        {
            string staged = path?.Trim() ?? string.Empty;

            if (resettingStaging || isPromptingForPath || RequestDatabasePasswordAsync is null)
            {
                return;
            }

            // The header read comes first: it is the only check here that also proves the path is
            // one the file APIs accept, which the comparison below assumes.
            if (staged.Length == 0 ||
                string.Equals(staged, lastPromptedPath, StringComparison.OrdinalIgnoreCase) ||
                !DatabaseService.IsEncryptedFile(staged) ||
                IsCurrentlyOpenDatabase(staged))
            {
                return;
            }

            isPromptingForPath = true;

            try
            {
                lastPromptedPath = staged;

                string? entered = await RequestDatabasePasswordAsync.Invoke(staged, false);

                // A path typed character by character can move on while the dialog is up; a
                // password for a file no longer staged must not be staged with it.
                if (!string.Equals(CandidatePath?.Trim(), staged, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!string.IsNullOrEmpty(entered))
                {
                    CandidatePassword = entered;

                    return;
                }

                // Cancelled. The box is cleared rather than left holding the previous database's
                // password, which would only produce a confusing failure on Apply.
                CandidatePassword = string.Empty;
                ErrorMessage = "That database is encrypted. Apply needs its password.";
            }
            finally
            {
                isPromptingForPath = false;
            }
        }

        partial void OnCandidatePasswordChanged(string value)
        {
            OnPropertyChanged(nameof(NeedsApply));
            OnPropertyChanged(nameof(ApplyIsEnabled));
            UpdateEncryptionNotice();
        }

        /// <summary>
        /// Reads the header of the staged file to see whether it is encrypted, and says so before
        /// Apply is pressed. The cases read differently to the user: an encrypted file whose
        /// password is already saved needs nothing typed, one without a saved password does, and an
        /// unencrypted file needs no notice at all.
        /// </summary>
        private void UpdateEncryptionNotice()
        {
            string path = CandidatePath?.Trim() ?? string.Empty;

            if (!DatabaseService.IsEncryptedFile(path))
            {
                EncryptionNotice = string.Empty;

                return;
            }

            // A saved password no longer earns a notice of its own: it is not used unprompted any
            // more, and the line it printed only repeated what the status line underneath already
            // says about the file being encrypted.
            if (DatabaseService.HasKnownPasswordFor(path))
            {
                EncryptionNotice = string.Empty;

                return;
            }

            EncryptionNotice = string.IsNullOrEmpty(CandidatePassword)
                ? "This database is encrypted. Enter its password below - Evania will ask for it otherwise."
                : "This database is encrypted. The password below will be used to unlock it.";
        }

        [RelayCommand]
        private async Task ApplyDatabaseAsync()
        {
            if (IsApplying || IsSuccess)
            {
                return;
            }

            ErrorMessage = string.Empty;

            // Normalise what was typed before it is compared to anything, so a stray space cannot
            // leave Apply looking needed forever.
            CandidatePath = (CandidatePath ?? string.Empty).Trim();

            // An empty box is a half-finished edit, not a request. Saying so here keeps the live
            // database open and untouched, which is what clearing a text box should cost.
            if (CandidatePath.Length == 0)
            {
                ErrorMessage = "Enter the path of a database file. The current database is still open.";

                return;
            }

            IsApplying = true;

            string targetPath = CandidatePath;
            bool applied = false;

            try
            {
                // An encrypted file whose password nothing on hand knows cannot be opened by trying,
                // so ask before the attempt rather than making the user read a LiteDB failure first.
                if (NeedsPasswordUpFront(targetPath))
                {
                    string? upFrontPassword = await RequestDatabasePasswordAsync!.Invoke(targetPath, false);

                    if (string.IsNullOrEmpty(upFrontPassword))
                    {
                        ErrorMessage = "That database is encrypted and cannot be opened without its password.";

                        return;
                    }

                    CandidatePassword = upFrontPassword;
                }

                DatabaseStatus result = await ApplyCandidateAsync(CandidatePassword);

                // A refused password is usually a typo, so offer the box again rather than only
                // reporting the failure. Three tries catches one without turning into a guessing
                // game against someone else's database.
                for (int attempt = 0; attempt < 3 && ShouldRequestPassword(targetPath, result); attempt++)
                {
                    string? enteredPassword = await RequestDatabasePasswordAsync!.Invoke(targetPath, true);

                    if (string.IsNullOrEmpty(enteredPassword))
                    {
                        break;
                    }

                    CandidatePassword = enteredPassword;
                    result = await ApplyCandidateAsync(CandidatePassword);
                }

                if (!result.Connected)
                {
                    // A rejected password is worth saying plainly: the LiteDB message underneath it
                    // reads as file corruption to anyone who has not seen it before.
                    ErrorMessage = ShouldRequestPassword(targetPath, result)
                        ? "That password does not open this database. Nothing was changed."
                        : result.Error ?? "The database could not be changed.";
                }
                else
                {
                    applied = true;
                }
            }
            finally
            {
                IsApplying = false;
                RefreshStatus();
            }

            if (applied)
            {
                // Confirm with a check before the button falls back to the plain label.
                IsSuccess = true;
                await Task.Delay(1600);
                IsSuccess = false;
            }
        }

        private Task<DatabaseStatus> ApplyCandidateAsync(string candidatePassword)
        {
            string? currentPassword = DatabaseService.CurrentPassword ?? Settings.DatabasePassword;
            bool passwordChanged = !string.Equals(candidatePassword, currentPassword ?? string.Empty, StringComparison.Ordinal);
            bool wantEncrypted = !string.IsNullOrEmpty(candidatePassword);

            return DatabaseService.ApplyAsync(
                CandidatePath,
                wantEncrypted,
                passwordChanged ? candidatePassword : currentPassword);
        }

        /// <summary>
        /// True when the staged file is encrypted and is not the one already open, in which case
        /// its password is always asked for.
        ///
        /// A saved password is deliberately no longer accepted as a reason to skip the prompt.
        /// Windows Credential Manager holding a key for a file only says this machine opened it
        /// once; it does not say the person at the keyboard now is the one who set it. Switching to
        /// a protected database is exactly the moment to ask. The password box is not consulted
        /// either - it is pre-filled with the open database's password, which says nothing about a
        /// different file.
        /// </summary>
        private bool NeedsPasswordUpFront(string path) =>
            RequestDatabasePasswordAsync is not null
            && DatabaseService.IsEncryptedFile(path)
            && !IsCurrentlyOpenDatabase(path)
            && !AlreadyPromptedFor(path);

        /// <summary>
        /// Whether staging this path already asked for its password and got one. Without this,
        /// choosing a protected database would ask once when it is picked and again on Apply.
        /// </summary>
        private bool AlreadyPromptedFor(string path) =>
            string.Equals(path?.Trim(), lastPromptedPath, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(CandidatePassword);

        /// <summary>
        /// Whether the staged path is the live connection's own file. Re-applying that one - to
        /// change its password, say - is not "selecting an encrypted database", and prompting for a
        /// key the session is already holding would only be noise.
        /// </summary>
        private static bool IsCurrentlyOpenDatabase(string path)
        {
            if (DatabaseService.CurrentPath is not string openPath)
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(openPath),
                    Path.GetFullPath(path),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // A path the file APIs reject is certainly not the one already open.
                return false;
            }
        }

        private bool ShouldRequestPassword(string path, DatabaseStatus result)
        {
            if (RequestDatabasePasswordAsync is null || result.Connected)
            {
                return false;
            }

            // The file header settles it outright; the message match below stays as the fallback for
            // files the header check cannot classify.
            if (DatabaseService.IsEncryptedFile(path))
            {
                return true;
            }

            string error = result.Error ?? string.Empty;

            return error.Contains("password", StringComparison.OrdinalIgnoreCase)
                || error.Contains("encrypted", StringComparison.OrdinalIgnoreCase)
                || error.Contains("invalid datafile", StringComparison.OrdinalIgnoreCase);
        }

        public async Task SetStartWithWindowsAsync(bool enabled)
        {
            ErrorMessage = string.Empty;

            string? error = await AutoStartService.SetEnabledAsync(enabled);

            if (error is null)
            {
                Settings.StartWithWindows = enabled;

                return;
            }

            ErrorMessage = error;

            // The operating system's answer wins over what was asked for, so the switch cannot end up
            // claiming a state Task Manager disagrees with.
            await AutoStartService.SyncSettingAsync();
        }

        public async Task SetCloseToTrayAsync(bool enabled)
        {
            bool previous = Settings.CloseToTray;

            // Apply the setting in memory first so the rest of the app sees the request, but be
            // prepared to roll it back if the tray icon cannot be created.
            Settings.CloseToTray = enabled;

            await TrayIconService.SetEnabledAsync(enabled);

            // ShowAsync swallows fatal errors to keep startup safe, so inspect the result here.
            if (enabled && !TrayIconService.IsShowing)
            {
                Settings.CloseToTray = previous;
                ErrorMessage = "Evania could not show its tray icon. The setting was turned back off.";
            }
        }

        public void SetAutoSave(bool enabled) => Settings.AutoSaveScreenshot = enabled;

        /// <summary>
        /// The Chat page listens to this setting itself, so a switch here moves a loaded chat
        /// straight to the new provider.
        /// </summary>
        public void SetChatProvider(ChatProvider provider) => Settings.ChatProvider = provider;

        public async Task<bool> AddBookmarkAsync()
        {
            if (IsAddingBookmark)
            {
                return false;
            }

            string candidateUrl = NewBookmarkUrl.Trim();

            if (!TryNormalizeBookmarkUrl(candidateUrl, out string? normalizedUrl))
            {
                ErrorMessage = "Enter a valid http or https URL.";
                return false;
            }

            if (Settings.Bookmarks.Any(b => string.Equals(b.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase)))
            {
                ErrorMessage = "That webpage is already in your bookmarks.";
                return false;
            }

            IsAddingBookmark = true;

            try
            {
                string title = await GetBookmarkTitleFromWebsiteAsync(normalizedUrl) ?? GetBookmarkFallbackTitle(normalizedUrl);

                Settings.Bookmarks =
                [
                    ..Settings.Bookmarks.Select(b => new WebBookmark
                    {
                        Title = b.Title,
                        Url = b.Url,
                    }),
                    new WebBookmark
                    {
                        Title = title,
                        Url = normalizedUrl,
                    }
                ];

                NewBookmarkUrl = string.Empty;
                ErrorMessage = string.Empty;
                return true;
            }
            finally
            {
                IsAddingBookmark = false;
            }
        }

        public void RemoveBookmark(WebBookmark bookmark)
        {
            Settings.Bookmarks = Settings.Bookmarks
                .Where(b => !string.Equals(b.Url, bookmark.Url, StringComparison.OrdinalIgnoreCase))
                .Select(b => new WebBookmark
                {
                    Title = b.Title,
                    Url = b.Url,
                })
                .ToList();

            ErrorMessage = string.Empty;
        }

        public void SetShortcut(ScreenshotHotkey hotkey)
        {
            string? error = GlobalHotkeyService.Apply(hotkey);

            stagingHotkey = true;
            SelectedHotkeyOption = HotkeyOptions.FirstOrDefault(
                o => o.Value == (error is null ? hotkey : GlobalHotkeyService.Registered)) ?? HotkeyOptions[0];
            stagingHotkey = false;

            if (error is not null)
            {
                // The dropdown follows SelectedHotkeyOption, so a rejected registration snaps the
                // selection back to what is actually registered.
                ErrorMessage = error;

                return;
            }

            ErrorMessage = string.Empty;
            Settings.ScreenshotShortcut = hotkey;
        }

        public void RefreshStatus()
        {
            DatabaseStatus status = DatabaseService.Status;

            StatusText = status.Description;

            OnPropertyChanged(nameof(NeedsApply));
            OnPropertyChanged(nameof(ApplyIsEnabled));
            UpdateEncryptionNotice();
        }

        private void SyncBookmarksFromSettings()
        {
            Bookmarks.Clear();

            foreach (WebBookmark bookmark in Settings.Bookmarks)
            {
                if (!TryNormalizeBookmarkUrl(bookmark.Url, out string? normalizedUrl))
                {
                    continue;
                }

                string title = string.IsNullOrWhiteSpace(bookmark.Title)
                    ? GetBookmarkFallbackTitle(normalizedUrl)
                    : bookmark.Title.Trim();

                Bookmarks.Add(new WebBookmark
                {
                    Title = title,
                    Url = normalizedUrl,
                });
            }

            OnPropertyChanged(nameof(HasBookmarks));
        }

        private static async Task<string?> GetBookmarkTitleFromWebsiteAsync(string url)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", BookmarkTitleUserAgent);

                using HttpResponseMessage response = await BookmarkTitleHttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead);

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string html = await response.Content.ReadAsStringAsync();
                Match titleMatch = HtmlTitleRegex.Match(html);

                if (!titleMatch.Success)
                {
                    return null;
                }

                string title = WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim();

                if (string.IsNullOrWhiteSpace(title))
                {
                    return null;
                }

                title = Regex.Replace(title, "\\s+", " ");

                if (HasInvalidBookmarkTitle(title))
                {
                    return null;
                }

                return title;
            }
            catch
            {
                return null;
            }
        }

        private static string GetBookmarkFallbackTitle(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                return uri.Host;
            }

            return url;
        }

        private static bool HasInvalidBookmarkTitle(string title)
        {
            string normalized = title.Trim().ToLowerInvariant();
            return InvalidBookmarkTitleFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
        }

        private static bool TryNormalizeBookmarkUrl(string value, [NotNullWhen(true)] out string? normalizedUrl)
        {
            normalizedUrl = null;

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            normalizedUrl = uri.AbsoluteUri;
            return true;
        }
    }
}

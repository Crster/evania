using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Evania.Models;
using Evania.Services;
using Evania.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Evania.Views;

public sealed partial class NotePage : Page
{
    private ScrollViewer? listScrollViewer;
    private ScrollViewer? editorScrollViewer;
    private string restoredListStateKey = string.Empty;
    private bool loadingDocument;

    public NotePageViewModel ViewModel { get; } = new();

    public NotePage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Editor.Paste += Editor_Paste;
        Editor.AddHandler(
            PointerPressedEvent,
            new PointerEventHandler(Editor_PointerPressed),
            true);
    }

    private const string AttachmentLinkPrefix = "evania://note/attachment/";

    private async void NotePage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
        await RestoreListPositionAsync();
        UpdatePanels();
    }

    private async void NotePage_Unloaded(object sender, RoutedEventArgs e)
    {
        await SaveScrollPositionsAsync();
        await ViewModel.FlushAsync();
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        await SaveListPositionAsync();
        await ViewModel.NewAsync();
        LoadCurrentDocument();
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        SaveEditorPosition();
        await ViewModel.CloseEditorAsync();
        UpdatePanels();
        await RestoreListPositionAsync();
    }

    private async void NoteList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (NoteList.SelectedItem is not NoteListItem item)
        {
            return;
        }

        await SaveListPositionAsync();
        await ViewModel.OpenAsync(item);
        LoadCurrentDocument();
    }

    private void Editor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (loadingDocument)
        {
            return;
        }

        Editor.Document.GetText(TextGetOptions.FormatRtf, out string richText);
        Editor.Document.GetText(TextGetOptions.None, out string plainText);
        ViewModel.UpdateDocument(richText, plainText);
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Tab)
        {
            return;
        }

        Editor.Document.Selection.Text = "\t";
        e.Handled = true;
    }

    private void Bold_Click(object sender, RoutedEventArgs e) =>
        Editor.Document.Selection.CharacterFormat.Bold = FormatEffect.Toggle;

    private void Italic_Click(object sender, RoutedEventArgs e) =>
        Editor.Document.Selection.CharacterFormat.Italic = FormatEffect.Toggle;

    private void Underline_Click(object sender, RoutedEventArgs e)
    {
        ITextCharacterFormat format = Editor.Document.Selection.CharacterFormat;
        format.Underline = format.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None;
    }

    private void Highlight_Click(object sender, RoutedEventArgs e)
    {
        ITextCharacterFormat format = Editor.Document.Selection.CharacterFormat;
        global::Windows.UI.Color highlight = GetHighlightColor();
        global::Windows.UI.Color current = format.BackgroundColor;
        bool isHighlighted = current.R == highlight.R && current.G == highlight.G && current.B == highlight.B;
        format.BackgroundColor = isHighlighted ? Microsoft.UI.Colors.Transparent : highlight;
    }

    private global::Windows.UI.Color GetHighlightColor()
    {
        bool isDark = ActualTheme == ElementTheme.Dark;
        global::Windows.UI.Color background = isDark
            ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32)
            : Microsoft.UI.Colors.White;
        const double opacity = 0.15;
        byte Blend(byte source, byte target) =>
            (byte)Math.Round(source + (target - source) * opacity);
        return Microsoft.UI.ColorHelper.FromArgb(
            255,
            Blend(background.R, 255),
            Blend(background.G, 255),
            Blend(background.B, 0));
    }

    private void Heading1_Click(object sender, RoutedEventArgs e) => ApplyHeading(28);

    private void Heading2_Click(object sender, RoutedEventArgs e) => ApplyHeading(22);

    private void Heading3_Click(object sender, RoutedEventArgs e) => ApplyHeading(18);

    private void Blockquote_Click(object sender, RoutedEventArgs e)
    {
        ITextRange selection = Editor.Document.Selection;
        ITextParagraphFormat paragraph = selection.ParagraphFormat;
        bool isQuoted = paragraph.LeftIndent >= 20;

        paragraph.SetIndents(0, isQuoted ? 0 : 24, 0);
        selection.CharacterFormat.ForegroundColor = isQuoted
            ? (ActualTheme == ElementTheme.Dark
                ? Microsoft.UI.Colors.White
                : Microsoft.UI.Colors.Black)
            : (ActualTheme == ElementTheme.Dark
                ? Microsoft.UI.ColorHelper.FromArgb(255, 176, 176, 176)
                : Microsoft.UI.ColorHelper.FromArgb(255, 90, 90, 90));
    }

    private void List_Click(object sender, RoutedEventArgs e)
    {
        ITextParagraphFormat paragraph = Editor.Document.Selection.ParagraphFormat;
        paragraph.ListType = paragraph.ListType == MarkerType.None ? MarkerType.Bullet : MarkerType.None;
    }

    private void OrderedList_Click(object sender, RoutedEventArgs e)
    {
        ITextParagraphFormat paragraph = Editor.Document.Selection.ParagraphFormat;
        paragraph.ListType = paragraph.ListType == MarkerType.None ? MarkerType.Arabic : MarkerType.None;
    }

    private void CodeBlock_Click(object sender, RoutedEventArgs e)
    {
        ITextRange selection = Editor.Document.Selection;
        selection.CharacterFormat.Name = "Consolas";
        selection.CharacterFormat.BackgroundColor = Microsoft.UI.ColorHelper.FromArgb(32, 128, 128, 128);
        selection.ParagraphFormat.SetIndents(0, 12, 0);
    }

    private async void Image_Click(object sender, RoutedEventArgs e)
    {
        StorageFile? file = await PickFileAsync(new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" });
        if (file is null)
        {
            return;
        }

        await AddAttachmentAsync(file, true);

        try
        {
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
            Editor.Document.Selection.InsertImage(
                0,
                0,
                0,
                VerticalCharacterAlignment.Baseline,
                file.Name,
                stream);
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"The image was attached but could not be embedded: {ex.Message}";
        }
    }

    private async void File_Click(object sender, RoutedEventArgs e)
    {
        StorageFile? file = await PickFileAsync(new[] { ".*" });
        if (file is not null)
        {
            await AddAttachmentAsync(file, false);
        }
    }

    private async void OpenAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NoteAttachmentItem item })
        {
            return;
        }

        await OpenAttachmentAsync(item);
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NoteAttachmentItem item })
        {
            ViewModel.RemoveAttachment(item);
        }
    }

    private async void Editor_Paste(object sender, TextControlPasteEventArgs e)
    {
        DataPackageView content = Clipboard.GetContent();

        if (content.Contains(StandardDataFormats.StorageItems))
        {
            e.Handled = true;
            IReadOnlyList<IStorageItem> items = await content.GetStorageItemsAsync();
            foreach (IStorageItem item in items)
            {
                if (item is not StorageFile file)
                {
                    continue;
                }

                NoteAttachmentItem? attachment = await AddAttachmentAsync(file, false);
                if (attachment is not null)
                {
                    InsertLink(file.Name, AttachmentLinkPrefix + attachment.Id);
                }
            }

            return;
        }

        string? url = null;
        if (content.Contains(StandardDataFormats.Text))
        {
            string text = (await content.GetTextAsync()).Trim();
            if (IsWebUrl(text))
            {
                url = text;
            }
        }

        if (url is null && content.Contains(StandardDataFormats.Html))
        {
            string html = await content.GetHtmlFormatAsync();
            Match match = Regex.Match(html, "href=[\"'](?<url>https?://[^\"']+)", RegexOptions.IgnoreCase);
            if (match.Success && IsWebUrl(match.Groups["url"].Value))
            {
                url = match.Groups["url"].Value;
            }
        }

        if (url is not null)
        {
            e.Handled = true;
            InsertLink(url, url);
        }
    }

    private static bool IsWebUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void InsertLink(string text, string url)
    {
        ITextSelection selection = Editor.Document.Selection;
        int start = selection.StartPosition;
        selection.Text = text;
        ITextRange range = Editor.Document.GetRange(start, start + text.Length);
        range.Link = $"\"{url}\"";
        selection.StartPosition = selection.EndPosition = start + text.Length;
        selection.Text = " ";
        selection.StartPosition = selection.EndPosition = start + text.Length + 1;
    }

    private async void Editor_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Microsoft.UI.Input.VirtualKeyStates controlState = (Microsoft.UI.Input.VirtualKeyStates)
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                global::Windows.System.VirtualKey.Control);
        if (!controlState.HasFlag(Microsoft.UI.Input.VirtualKeyStates.Down))
        {
            return;
        }

        System.Diagnostics.Debug.WriteLine("[NotePage] Ctrl+PointerPressed detected");
        try
        {
            Thickness padding = Editor.Padding;
            global::Windows.Foundation.Point raw = e.GetCurrentPoint(Editor).Position;
            global::Windows.Foundation.Point point = new(raw.X - padding.Left, raw.Y - padding.Top);
            ITextRange range = Editor.Document.GetRangeFromPoint(point, PointOptions.ClientCoordinates);
            string link = GetLinkAt(range);
            System.Diagnostics.Debug.WriteLine($"[NotePage] Link resolved: '{link}'");

            // Always swallow Ctrl+Click so the RichEditBox doesn't launch the URI itself.
            e.Handled = true;

            if (link.Length > 0)
            {
                await OpenLinkAsync(link);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NotePage] Ctrl+Click failed: {ex}");
        }
    }

    private string GetLinkAt(ITextRange range)
    {
        string link = string.Empty;
        ITextRange probe = range.GetClone();
        for (int i = 0; i < 32 && link.Length == 0; i++)
        {
            probe.MoveStart(TextRangeUnit.Character, -1);
            probe.MoveEnd(TextRangeUnit.Character, 1);
            link = probe.Link;
        }

        if (link.Length == 0)
        {
            range.Expand(TextRangeUnit.Word);
            link = range.Link;
        }

        System.Diagnostics.Debug.WriteLine($"[NotePage] Raw link value: '{link}'");
        if (link.Length >= 2 && link.StartsWith('"') && link.EndsWith('"'))
        {
            link = link.Substring(1, link.Length - 2);
        }

        return link;
    }

    private static async Task OpenLinkAsync(string link)
    {
        // Attachment links are registered to this app; launching them routes back into
        // App.TryOpenAttachmentProtocolAsync, which opens the file with its default app.
        if (link.StartsWith(AttachmentLinkPrefix, StringComparison.OrdinalIgnoreCase) || IsWebUrl(link))
        {
            System.Diagnostics.Debug.WriteLine($"[NotePage] Launching link: {link}");
            await global::Windows.System.Launcher.LaunchUriAsync(new Uri(link));
        }
    }

    private async Task OpenAttachmentAsync(NoteAttachmentItem item)
    {
        byte[]? bytes = await ViewModel.ReadAttachmentAsync(item);
        if (bytes is null)
        {
            System.Diagnostics.Debug.WriteLine("[NotePage] Attachment bytes were null");
            return;
        }

        StorageFile file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
            item.FileName,
            CreationCollisionOption.GenerateUniqueName);
        await FileIO.WriteBytesAsync(file, bytes);
        System.Diagnostics.Debug.WriteLine($"[NotePage] Launching file: {file.Path}");
        await global::Windows.System.Launcher.LaunchFileAsync(file);
    }

    private void ErrorBar_Closed(InfoBar sender, object args) => ViewModel.ClearError();

    private async void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotePageViewModel.IsEditing))
        {
            UpdatePanels();
        }
        else if (e.PropertyName == nameof(NotePageViewModel.ListStateKey) && !ViewModel.IsEditing)
        {
            await SaveListPositionAsync();
            await RestoreListPositionAsync();
        }
    }

    private void LoadCurrentDocument()
    {
        loadingDocument = true;
        try
        {
            Editor.Document.SetText(TextSetOptions.FormatRtf, ViewModel.CurrentRichText);

            Editor.Document.GetText(TextGetOptions.None, out string plainText);
            if (string.IsNullOrWhiteSpace(plainText))
            {
                ResetEditorFormatting();
            }

            UpdatePanels();
            DispatcherQueue.TryEnqueue(() =>
            {
                editorScrollViewer ??= FindScrollViewer(Editor);
                editorScrollViewer?.ChangeView(null, ViewModel.CurrentEditorScrollOffset, null, true);
                Editor.Focus(FocusState.Programmatic);
            });
        }
        finally
        {
            loadingDocument = false;
        }
    }

    private void UpdatePanels()
    {
        ListPanel.Visibility = ViewModel.IsEditing ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResetEditorFormatting()
    {
        ITextRange wholeDocument = Editor.Document.GetRange(0, int.MaxValue);
        ITextCharacterFormat characterFormat = wholeDocument.CharacterFormat;
        characterFormat.Bold = FormatEffect.Off;
        characterFormat.Italic = FormatEffect.Off;
        characterFormat.Underline = UnderlineType.None;
        characterFormat.Size = 14;
        characterFormat.Name = "Segoe UI";
        characterFormat.ForegroundColor = ActualTheme == ElementTheme.Dark
            ? Microsoft.UI.Colors.White
            : Microsoft.UI.Colors.Black;
        characterFormat.BackgroundColor = Microsoft.UI.Colors.Transparent;

        ITextParagraphFormat paragraphFormat = wholeDocument.ParagraphFormat;
        paragraphFormat.SetIndents(0, 0, 0);
        paragraphFormat.ListType = MarkerType.None;
    }

    private void ApplyHeading(float size)
    {
        ITextRange selection = Editor.Document.Selection;
        selection.CharacterFormat.Size = size;
        selection.CharacterFormat.Bold = FormatEffect.On;
    }

    private async Task<NoteAttachmentItem?> AddAttachmentAsync(StorageFile file, bool isImage)
    {
        using Stream stream = await file.OpenStreamForReadAsync();
        return await ViewModel.AddAttachmentAsync(file.Name, file.ContentType, isImage, stream);
    }

    private async Task<StorageFile?> PickFileAsync(string[] extensions)
    {
        if (App.MainWindow is null)
        {
            return null;
        }

        FileOpenPicker picker = new();
        foreach (string extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        return await picker.PickSingleFileAsync();
    }

    private async Task SaveScrollPositionsAsync()
    {
        SaveEditorPosition();
        await SaveListPositionAsync();
    }

    private void SaveEditorPosition()
    {
        editorScrollViewer ??= FindScrollViewer(Editor);
        if (editorScrollViewer is not null)
        {
            ViewModel.UpdateEditorScrollOffset(editorScrollViewer.VerticalOffset);
        }
    }

    private async Task SaveListPositionAsync()
    {
        listScrollViewer ??= FindScrollViewer(NoteList);
        if (listScrollViewer is not null && restoredListStateKey.Length > 0)
        {
            await NoteService.SaveListScrollOffsetAsync(restoredListStateKey, listScrollViewer.VerticalOffset);
        }
    }

    private async Task RestoreListPositionAsync()
    {
        string stateKey = ViewModel.ListStateKey;
        double offset = await NoteService.GetListScrollOffsetAsync(stateKey);
        restoredListStateKey = stateKey;

        await Task.Yield();
        listScrollViewer ??= FindScrollViewer(NoteList);
        listScrollViewer?.ChangeView(null, offset, null, true);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            ScrollViewer? nested = FindScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}

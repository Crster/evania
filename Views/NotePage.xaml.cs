using System;
using System.IO;
using System.Threading.Tasks;
using Evania.Models;
using Evania.Services;
using Evania.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Evania.Views;

public sealed partial class NotePage : Page
{
    /// <summary>
    /// Attachment links are registered to this app, so launching one routes back into
    /// App.TryOpenAttachmentProtocolAsync, which opens the file with its default handler.
    /// </summary>
    private const string AttachmentLinkPrefix = "evania://note/attachment/";

    private ScrollViewer? listScrollViewer;
    private string restoredListStateKey = string.Empty;
    private bool loadingDocument;

    public NotePageViewModel ViewModel { get; } = new();

    public NotePage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

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

    /// <summary>
    /// The editor owns the markdown; the plain-text projection is stored alongside it for the note
    /// list previews and search.
    /// </summary>
    private void Editor_MarkdownChanged(object? sender, string markdown)
    {
        if (loadingDocument)
        {
            return;
        }

        ViewModel.UpdateDocument(markdown, Editor.PlainText);
    }

    private async void Editor_LinkInvoked(object? sender, string target) => await OpenLinkAsync(target);

    // --- toolbar ------------------------------------------------------------------------------

    private void Bold_Click(object sender, RoutedEventArgs e) => Editor.ToggleBold();

    private void Italic_Click(object sender, RoutedEventArgs e) => Editor.ToggleItalic();

    private void Strikethrough_Click(object sender, RoutedEventArgs e) => Editor.ToggleStrikethrough();

    private void Highlight_Click(object sender, RoutedEventArgs e) => Editor.ToggleHighlight();

    private void Heading1_Click(object sender, RoutedEventArgs e) => Editor.ApplyHeading(1);

    private void Heading2_Click(object sender, RoutedEventArgs e) => Editor.ApplyHeading(2);

    private void Heading3_Click(object sender, RoutedEventArgs e) => Editor.ApplyHeading(3);

    private void Blockquote_Click(object sender, RoutedEventArgs e) => Editor.ToggleQuote();

    private void List_Click(object sender, RoutedEventArgs e) => Editor.ToggleBulletList();

    private void OrderedList_Click(object sender, RoutedEventArgs e) => Editor.ToggleOrderedList();

    private void TaskList_Click(object sender, RoutedEventArgs e) => Editor.ToggleTaskList();

    private void Table_Click(object sender, RoutedEventArgs e) => Editor.InsertTable();

    private void HorizontalRule_Click(object sender, RoutedEventArgs e) => Editor.InsertHorizontalRule();

    private void CodeBlock_Click(object sender, RoutedEventArgs e) => Editor.ToggleCodeBlock();

    /// <summary>
    /// Images become attachments referenced through the app's attachment protocol, so the markdown
    /// stays portable and the bytes stay in the database instead of being embedded in the note.
    /// </summary>
    private async void Image_Click(object sender, RoutedEventArgs e)
    {
        StorageFile? file = await PickFileAsync(new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" });
        if (file is null)
        {
            return;
        }

        NoteAttachmentItem? attachment = await AddAttachmentAsync(file, true);
        if (attachment is not null)
        {
            Editor.InsertImage(AttachmentLinkPrefix + attachment.Id, file.Name);
        }
    }

    private async void File_Click(object sender, RoutedEventArgs e)
    {
        StorageFile? file = await PickFileAsync(new[] { ".*" });
        if (file is null)
        {
            return;
        }

        NoteAttachmentItem? attachment = await AddAttachmentAsync(file, false);
        if (attachment is not null)
        {
            Editor.AppendAttachmentLink(file.Name, AttachmentLinkPrefix + attachment.Id, isImage: false);
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

    // --- links and attachments ----------------------------------------------------------------

    private static bool IsWebUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static async Task OpenLinkAsync(string link)
    {
        if (link.StartsWith(AttachmentLinkPrefix, StringComparison.OrdinalIgnoreCase) || IsWebUrl(link))
        {
            await global::Windows.System.Launcher.LaunchUriAsync(new Uri(link));
        }
    }

    private async Task OpenAttachmentAsync(NoteAttachmentItem item)
    {
        byte[]? bytes = await ViewModel.ReadAttachmentAsync(item);
        if (bytes is null)
        {
            return;
        }

        StorageFile file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
            item.FileName,
            CreationCollisionOption.GenerateUniqueName);
        await FileIO.WriteBytesAsync(file, bytes);
        await global::Windows.System.Launcher.LaunchFileAsync(file);
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

    // --- document / panels --------------------------------------------------------------------

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
            Editor.Markdown = ViewModel.CurrentMarkdown;
            UpdatePanels();

            // The editor measures itself on the next layout pass, so restoring scroll and focus has to
            // wait until after that pass or the offset is clamped to a zero-height surface.
            DispatcherQueue.TryEnqueue(() =>
            {
                Editor.VerticalScrollOffset = ViewModel.CurrentEditorScrollOffset;
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

    // --- scroll persistence -------------------------------------------------------------------

    private async Task SaveScrollPositionsAsync()
    {
        SaveEditorPosition();
        await SaveListPositionAsync();
    }

    private void SaveEditorPosition() => ViewModel.UpdateEditorScrollOffset(Editor.VerticalScrollOffset);

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

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Evania.Controls.MarkdownEditor;
using Evania.Models;
using Evania.Services;
using Evania.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
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
        SortToggle.IsChecked = ViewModel.SelectedSort == NoteSortOrder.CreatedDescending;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Editor.ImageResolver = ResolveImageAsync;
        Editor.ClipboardPasteHandler = PasteAttachmentAsync;
        Editor.LinkRenamed += Editor_LinkRenamed;
        Editor.ViewportChanged += Editor_ViewportChanged;
    }

    /// <summary>
    /// Loads an editor image by its markdown target. Only attachment URIs are backed by the database;
    /// every other target (a plain URL or a path the editor has no bytes for) returns null and keeps
    /// its placeholder.
    /// </summary>
    private async Task<byte[]?> ResolveImageAsync(string target)
    {
        if (!target.StartsWith(AttachmentLinkPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string id = Uri.UnescapeDataString(target[AttachmentLinkPrefix.Length..]);
        if (id.Length == 0)
        {
            return null;
        }

        try
        {
            return await NoteService.ReadAttachmentAsync(id);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async void NotePage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
        await RestoreListPositionAsync();
        UpdatePanels();

        // The window owns the search box in its chrome; it pushes text changes to us.
        if (App.MainWindow is Evania.Windows.MainWindow mainWindow)
        {
            mainWindow.TitleBarSearchChanged += MainWindow_TitleBarSearchChanged;
            Editor.SearchQuery = mainWindow.TitleBarSearchText;
        }
    }

    private void MainWindow_TitleBarSearchChanged(object? sender, string text)
    {
        Editor.SearchQuery = text;

        if (!ViewModel.IsEditing)
        {
            ViewModel.Filter = text;
        }
    }

    private async void NotePage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is Evania.Windows.MainWindow mainWindow)
        {
            mainWindow.TitleBarSearchChanged -= MainWindow_TitleBarSearchChanged;
        }

        await SaveScrollPositionsAsync();
        await ViewModel.FlushAsync();
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        await SaveListPositionAsync();
        await ViewModel.NewAsync();
        LoadCurrentDocument();
    }

    private async void SortToggle_Click(object sender, RoutedEventArgs e)
    {
        await SaveListPositionAsync();
        ViewModel.SelectedSort = SortToggle.IsChecked == true
            ? NoteSortOrder.CreatedDescending
            : NoteSortOrder.UpdatedDescending;
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.UpdateDocument(Editor.Markdown, Editor.PlainText);
        SaveEditorPosition();
        await ViewModel.CloseEditorAsync();
        UpdatePanels();
        await RestoreListPositionAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Delete note?",
            Content = "This will permanently delete the note and its attachments.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.DeleteCurrentAsync();
        if (!ViewModel.IsEditing)
        {
            UpdatePanels();
            await RestoreListPositionAsync();
        }
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
    /// A single tap opens a note; the list is the only content on the page, so the extra
    /// double-tap just made opening feel delayed.
    /// </summary>
    private async void NoteList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not NoteListItem item)
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

    private async void Editor_ViewportChanged(object? sender, MarkdownEditorViewport viewport)
    {
        if (!loadingDocument && ViewModel.IsEditing)
        {
            await ViewModel.SaveEditorViewportAsync(viewport);
        }
    }

    private async void Editor_LinkInvoked(object? sender, string target) => await OpenLinkAsync(target);

        // --- toolbar ------------------------------------------------------------------------------

    private void Bold_Click(object sender, RoutedEventArgs e) => Editor.ToggleBold();

    private void Italic_Click(object sender, RoutedEventArgs e) => Editor.ToggleItalic();

    private void Strikethrough_Click(object sender, RoutedEventArgs e) => Editor.ToggleStrikethrough();

    private void Highlight_Click(object sender, RoutedEventArgs e) => Editor.ToggleHighlight();

    private void Code_Click(object sender, RoutedEventArgs e) => Editor.ToggleInlineCode();

    private void Heading1_Click(object sender, RoutedEventArgs e) => Editor.ApplyHeading(1);

    private void Heading2_Click(object sender, RoutedEventArgs e) => Editor.ApplyHeading(2);

    private void Heading3_Click(object sender, RoutedEventArgs e) => Editor.ApplyHeading(3);

    private void Blockquote_Click(object sender, RoutedEventArgs e) => Editor.ToggleQuote();

    private void List_Click(object sender, RoutedEventArgs e) => Editor.ToggleBulletList();

    private void OrderedList_Click(object sender, RoutedEventArgs e) => Editor.ToggleOrderedList();

    private void TaskList_Click(object sender, RoutedEventArgs e) => Editor.ToggleTaskList();

    private void Table_Click(object sender, RoutedEventArgs e) => Editor.InsertTable();

    private void HorizontalRule_Click(object sender, RoutedEventArgs e) => Editor.InsertHorizontalRule();

    private void DateTime_Click(object sender, RoutedEventArgs e) => Editor.InsertDateTime();

    private void Uuid_Click(object sender, RoutedEventArgs e) => Editor.InsertUuid7();

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
        StorageFile? file = await PickFileAsync(new[] { "*" });
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

    /// <summary>
    /// Turns clipboard content that is not text into attachments: a pasted bitmap (screenshot or
    /// "copy image") is stored as a picture and inserted as an image, and files copied in the shell
    /// are attached - pictures as images, everything else as a link. All of the markdown goes back
    /// to the editor in one string so a multi-file paste lands as one edit.
    /// </summary>
    private async Task<string?> PasteAttachmentAsync(ClipboardPasteContent content)
    {
        if (content.ImageBytes is byte[] bytes)
        {
            string contentType = content.ContentType.Length > 0 ? content.ContentType : "image/png";
            string fileName = $"Pasted {DateTime.Now:yyyy-MM-dd HH-mm-ss}{ExtensionForContentType(contentType)}";
            NoteAttachmentItem? attachment = await ViewModel.AddAttachmentAsync(
                fileName,
                contentType,
                isImage: true,
                new MemoryStream(bytes));
            return attachment is null ? null : $"![{fileName}]({AttachmentLinkPrefix}{attachment.Id})";
        }

        var builder = new System.Text.StringBuilder();
        foreach (StorageFile file in content.Files)
        {
            bool isImage = IsImageFile(file.FileType);
            NoteAttachmentItem? attachment = await AddAttachmentAsync(file, isImage);
            if (attachment is null)
            {
                continue;
            }

            string target = AttachmentLinkPrefix + attachment.Id;
            builder.AppendLine(isImage ? $"![{file.Name}]({target})" : $"[{file.Name}]({target})");
        }

        string markdown = builder.ToString().TrimEnd();
        return markdown.Length == 0 ? null : markdown;
    }

    /// <summary>
    /// The extension to store a pasted bitmap under, derived from the clipboard's MIME type.
    /// Anything unrecognised becomes PNG, which is what screenshot tools put on the clipboard.
    /// </summary>
    private static string ExtensionForContentType(string contentType) => contentType switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/webp" => ".webp",
        _ => ".png",
    };

    private static bool IsImageFile(string extension)
        => new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" }.Contains(extension, StringComparer.OrdinalIgnoreCase);

    private async void OpenAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NoteAttachmentItem item })
        {
            return;
        }

        await OpenAttachmentAsync(item);
    }

    private async void AttachmentOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NoteAttachmentItem item })
        {
            await OpenAttachmentAsync(item);
        }
    }

    private void AttachmentCopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NoteAttachmentItem item })
        {
            string target = AttachmentLinkPrefix + item.Id;
            DataPackage package = new();
            package.SetText(target);
            Clipboard.SetContent(package);
        }
    }

    private async void AttachmentRename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NoteAttachmentItem item } || XamlRoot is null)
        {
            return;
        }

        TextBox textBox = new()
        {
            Text = item.FileName,
            Margin = new Thickness(0, 8, 0, 0)
        };
        textBox.Loaded += (_, _) => textBox.SelectAll();

        ContentDialog dialog = new()
        {
            Title = "Rename attachment",
            Content = textBox,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            string newName = textBox.Text.Trim();
            string target = AttachmentLinkPrefix + item.Id;
            ViewModel.RenameAttachment(item, newName);
            Editor.RenameAttachmentLinks(target, newName);
        }
    }

    private void Editor_LinkRenamed(object? sender, LinkRenamedEventArgs e)
    {
        if (e.Target.StartsWith(AttachmentLinkPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string id = Uri.UnescapeDataString(e.Target[AttachmentLinkPrefix.Length..]);
            NoteAttachmentItem? item = ViewModel.Attachments.FirstOrDefault(a => a.Id == id);
            if (item is not null)
            {
                ViewModel.RenameAttachment(item, e.NewLabel);
            }
        }
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NoteAttachmentItem item })
        {
            ViewModel.RemoveAttachment(item);
        }
    }

    private void AttachmentPill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NoteAttachmentItem item })
        {
            return;
        }

        string target = AttachmentLinkPrefix + item.Id;
        if (item.IsImage)
        {
            Editor.InsertImage(target, item.FileName);
        }
        else
        {
            Editor.AppendAttachmentLink(item.FileName, target, isImage: false);
        }
    }

    private void AttachmentPillDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NoteAttachmentItem item })
        {
            return;
        }

        string target = AttachmentLinkPrefix + item.Id;
        RemoveAttachmentLinkFromMarkdown(target);
        ViewModel.RemoveAttachment(item);
    }

    /// <summary>
    /// Removes all markdown links and images that reference <paramref name="target"/>.
    /// </summary>
    private void RemoveAttachmentLinkFromMarkdown(string target)
    {
        string markdown = Editor.Markdown;
        if (markdown.Length == 0)
        {
            return;
        }

        int pos = 0;
        while ((pos = markdown.IndexOf(target, pos, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            // Find the start of the enclosing link or image syntax.
            int linkStart = pos;
            int linkEnd = pos + target.Length;

            if (linkStart > 0 && markdown[linkStart - 1] == '(')
            {
                linkStart--; // include '('
                if (linkEnd < markdown.Length && markdown[linkEnd] == ')')
                {
                    linkEnd++; // include ')'
                }

                // Walk back to find '[' or '!['.
                if (linkStart > 0 && markdown[linkStart - 1] is ']')
                {
                    int bracketDepth = 1;
                    int scan = linkStart - 2;
                    while (scan >= 0)
                    {
                        if (markdown[scan] == ']')
                        {
                            bracketDepth++;
                        }
                        else if (markdown[scan] == '[')
                        {
                            bracketDepth--;
                            if (bracketDepth == 0)
                            {
                                if (scan > 0 && markdown[scan - 1] == '!')
                                {
                                    scan--;
                                }

                                linkStart = scan;
                                break;
                            }
                        }

                        scan--;
                    }
                }
            }

            // Remove the link/image plus the preceding newline if it exists.
            if (linkStart > 0 && markdown[linkStart - 1] == '\n')
            {
                linkStart--;
            }

            string before = markdown[..linkStart];
            string after = linkEnd < markdown.Length ? markdown[linkEnd..] : string.Empty;
            markdown = before + after;
            pos = linkStart;
        }

        Editor.Markdown = markdown;
    }

    // --- links and attachments ----------------------------------------------------------------

    private static bool IsWebUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private async Task OpenLinkAsync(string link)
    {
        if (link.StartsWith(AttachmentLinkPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string id = Uri.UnescapeDataString(link[AttachmentLinkPrefix.Length..]);
            await OpenAttachmentByIdAsync(id);
        }
        else if (IsWebUrl(link))
        {
            await global::Windows.System.Launcher.LaunchUriAsync(new Uri(link));
        }
    }

    private async Task OpenAttachmentByIdAsync(string id)
    {
        try
        {
            byte[] bytes = await NoteService.ReadAttachmentAsync(id);
            string fileName = await NoteService.GetAttachmentFileNameAsync(id);

            StorageFile file = await CreateTempFileAsync(fileName, bytes);
            await global::Windows.System.Launcher.LaunchFileAsync(file);
        }
        catch (Exception)
        {
        }
    }

    private async Task OpenAttachmentAsync(NoteAttachmentItem item)
    {
        byte[]? bytes = await ViewModel.ReadAttachmentAsync(item);
        if (bytes is null)
        {
            return;
        }

        StorageFile file = await CreateTempFileAsync(item.FileName, bytes);
        await global::Windows.System.Launcher.LaunchFileAsync(file);
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to a temporary file, normalising the name so
    /// <see cref="global::Windows.System.Launcher.LaunchFileAsync"/> can find a handler even for
    /// dotfiles like <c>.gitignore</c> that have no Windows file extension.
    /// </summary>
    private static async Task<StorageFile> CreateTempFileAsync(string fileName, byte[] bytes)
    {
        string safeName = NormalizeTempFileName(fileName);
        StorageFile file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
            safeName,
            CreationCollisionOption.GenerateUniqueName);
        await FileIO.WriteBytesAsync(file, bytes);
        return file;
    }

    /// <summary>
    /// Dotfiles have no extension in Windows, so <c>LaunchFileAsync</c> cannot pick a handler.
    /// Appending <c>.txt</c> gives them a recognisable type so they open in a text editor.
    /// </summary>
    private static string NormalizeTempFileName(string fileName)
    {
        return fileName.Length > 0 && fileName[0] == '.' && fileName.IndexOf('.', 1) < 0
            ? fileName + ".txt"
            : fileName;
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
                Editor.RestoreViewport(ViewModel.CurrentEditorViewport);
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

    private void SaveEditorPosition() => ViewModel.UpdateEditorViewport(Editor.CaptureViewport());

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

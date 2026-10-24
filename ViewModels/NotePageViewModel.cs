using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Evania.Controls.MarkdownEditor;
using Evania.Models;
using Evania.Services;

namespace Evania.ViewModels;

public partial class NotePageViewModel : ObservableObject, IDisposable
{
    private readonly HashSet<string> removedAttachmentIds = new();
    private CancellationTokenSource? saveDelay;
    private NoteDocument? currentNote;
    private bool loadingEditor;
    private bool noteDirty;

    public ObservableCollection<NoteListItem> Notes { get; } = new();

    public ObservableCollection<NoteAttachmentItem> Attachments { get; } = new();

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial NoteSortOrder SelectedSort { get; set; } = SettingsService.Current.NoteSort;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool IsEmpty => !IsBusy && Notes.Count == 0;

    public bool HasError => ErrorMessage.Length > 0;

    public string ListStateKey => NoteService.CreateListStateKey(Filter, SelectedSort);

    public NoteDocument? CurrentNote => currentNote;

    /// <summary>
    /// Markdown source for the editor. Notes created before the markdown editor only have RTF, which
    /// the editor cannot render, so they open with their stored plain text instead of appearing empty.
    /// </summary>
    public string CurrentMarkdown => currentNote is null
        ? string.Empty
        : !string.IsNullOrEmpty(currentNote.Markdown) ? currentNote.Markdown : currentNote.PlainText ?? string.Empty;

    public double CurrentEditorScrollOffset => currentNote?.EditorScrollOffset ?? 0;

    public MarkdownEditorViewport CurrentEditorViewport => currentNote is null
        ? default
        : new MarkdownEditorViewport(
            currentNote.EditorScrollOffset,
            currentNote.EditorViewportHeight,
            currentNote.EditorViewportSourcePosition,
            currentNote.EditorViewportAnchorOffset,
            currentNote.EditorViewportTopPosition);

    partial void OnTitleChanged(string value)
    {
        if (!loadingEditor && currentNote is not null)
        {
            currentNote.Title = value;
            noteDirty = true;
            ScheduleSave();
        }
    }

    partial void OnFilterChanged(string value) => _ = ReloadListAsync();

    partial void OnSelectedSortChanged(NoteSortOrder value)
    {
        SettingsService.Current.NoteSort = value;
        _ = ReloadListAsync();
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    public async Task LoadAsync() => await ReloadListAsync();

    public async Task NewAsync()
    {
        await FlushAsync();

        try
        {
            BeginLoadingEditor();
            currentNote = await NoteService.CreateAsync();
            Title = string.Empty;
            Attachments.Clear();
            removedAttachmentIds.Clear();
            IsEditing = true;
            OnPropertyChanged(nameof(CurrentNote));
            OnPropertyChanged(nameof(CurrentMarkdown));
            OnPropertyChanged(nameof(CurrentEditorScrollOffset));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            EndLoadingEditor();
        }
    }

    public async Task OpenAsync(NoteListItem item)
    {
        await FlushAsync();

        try
        {
            BeginLoadingEditor();
            currentNote = await NoteService.GetAsync(item.Id);
            if (currentNote is null)
            {
                await ReloadListAsync();
                return;
            }


            Title = currentNote.Title;
            Attachments.Clear();
            foreach (NoteAttachment attachment in currentNote.Attachments)
            {
                Attachments.Add(ToItem(attachment));
            }

            removedAttachmentIds.Clear();
            IsEditing = true;
            OnPropertyChanged(nameof(CurrentNote));
            OnPropertyChanged(nameof(CurrentMarkdown));
            OnPropertyChanged(nameof(CurrentEditorScrollOffset));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            EndLoadingEditor();
        }
    }

    public async Task CloseEditorAsync()
    {
        await FlushAsync();
        IsEditing = false;
        currentNote = null;
        Attachments.Clear();
        await ReloadListAsync();
    }

    public async Task DeleteCurrentAsync()
    {
        NoteDocument? note = currentNote;
        if (note is null)
        {
            return;
        }

        saveDelay?.Cancel();
        saveDelay?.Dispose();
        saveDelay = null;

        try
        {
            IsSaving = true;
            await NoteService.DeleteAsync(note);
            IsEditing = false;
            currentNote = null;
            noteDirty = false;
            Attachments.Clear();
            removedAttachmentIds.Clear();
            await ReloadListAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    public void UpdateDocument(string markdown, string plainText)
    {
        if (loadingEditor || currentNote is null)
        {
            return;
        }

        currentNote.Markdown = markdown;
        currentNote.PlainText = plainText.TrimEnd('\r', '\n');
        noteDirty = true;
        ScheduleSave();
    }

    public void UpdateEditorViewport(MarkdownEditorViewport viewport)
    {
        if (loadingEditor || currentNote is null)
        {
            return;
        }

        currentNote.EditorScrollOffset = Math.Max(0, viewport.ScrollOffset);
        currentNote.EditorViewportHeight = Math.Max(0, viewport.Height);
        currentNote.EditorViewportSourcePosition = viewport.SourcePosition;
        currentNote.EditorViewportAnchorOffset = viewport.AnchorOffset;
        currentNote.EditorViewportTopPosition = viewport.TopPosition;
        ScheduleSave();
    }

    public async Task SaveEditorViewportAsync(MarkdownEditorViewport viewport)
    {
        UpdateEditorViewport(viewport);
        if (currentNote is not null)
        {
            await NoteService.SaveViewportAsync(currentNote);
        }
    }

    public async Task<NoteAttachmentItem?> AddAttachmentAsync(string fileName, string contentType, bool isImage, System.IO.Stream source)
    {
        if (currentNote is null)
        {
            return null;
        }

        try
        {
            NoteAttachment attachment = await NoteService.AddAttachmentAsync(
                currentNote.Id,
                fileName,
                contentType,
                isImage,
                source);

            currentNote.Attachments.Add(attachment);
            NoteAttachmentItem item = ToItem(attachment);
            Attachments.Add(item);
            noteDirty = true;
            ScheduleSave();
            return item;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return null;
        }
    }

    public void RemoveAttachment(NoteAttachmentItem item)
    {
        if (currentNote is null)
        {
            return;
        }

        NoteAttachment? attachment = currentNote.Attachments.FirstOrDefault(value => value.Id == item.Id);
        if (attachment is null)
        {
            return;
        }

        currentNote.Attachments.Remove(attachment);
        Attachments.Remove(item);
        removedAttachmentIds.Add(item.Id);
        noteDirty = true;
        ScheduleSave();
    }

    public void RenameAttachment(NoteAttachmentItem item, string newFileName)
    {
        if (currentNote is null || string.IsNullOrWhiteSpace(newFileName))
        {
            return;
        }

        NoteAttachment? attachment = currentNote.Attachments.FirstOrDefault(value => value.Id == item.Id);
        if (attachment is not null)
        {
            attachment.FileName = newFileName;
        }

        item.FileName = newFileName;
        noteDirty = true;
        ScheduleSave();
    }

    public async Task<byte[]?> ReadAttachmentAsync(NoteAttachmentItem item)
    {
        try
        {
            return await NoteService.ReadAttachmentAsync(item.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return null;
        }
    }

    public async Task FlushAsync()
    {
        saveDelay?.Cancel();
        saveDelay?.Dispose();
        saveDelay = null;

        if (currentNote is null)
        {
            return;
        }

        await SaveCurrentAsync();
    }

    public void ClearError() => ErrorMessage = string.Empty;

    public void Dispose()
    {
        saveDelay?.Cancel();
        saveDelay?.Dispose();
    }

    private async Task ReloadListAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = string.Empty;
            IReadOnlyList<NoteListItem> notes = await NoteService.ListAsync(Filter, SelectedSort);

            Notes.Clear();
            foreach (NoteListItem note in notes)
            {
                Notes.Add(note);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ListStateKey));
        }
    }

    private void ScheduleSave()
    {
        saveDelay?.Cancel();
        saveDelay?.Dispose();
        saveDelay = new CancellationTokenSource();
        CancellationToken token = saveDelay.Token;
        _ = SaveAfterDelayAsync(token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(600, token);
            await SaveCurrentAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SaveCurrentAsync()
    {
        NoteDocument? note = currentNote;
        if (note is null)
        {
            return;
        }

        try
        {
            IsSaving = true;
            string[] removed = removedAttachmentIds.ToArray();
            await NoteService.SaveAsync(note, removed, bumpUpdatedAt: noteDirty);
            removedAttachmentIds.ExceptWith(removed);
            noteDirty = false;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void BeginLoadingEditor() => loadingEditor = true;

    private void EndLoadingEditor() => loadingEditor = false;

    private static NoteAttachmentItem ToItem(NoteAttachment attachment) => new()
    {
        Id = attachment.Id,
        FileName = attachment.FileName,
        ContentType = attachment.ContentType,
        Length = attachment.Length,
        IsImage = attachment.IsImage,
    };
}

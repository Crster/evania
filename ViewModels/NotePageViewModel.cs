using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Evania.Models;
using Evania.Services;

namespace Evania.ViewModels;

public partial class NotePageViewModel : ObservableObject, IDisposable
{
    private readonly HashSet<string> removedAttachmentIds = new();
    private CancellationTokenSource? saveDelay;
    private NoteDocument? currentNote;
    private bool loadingEditor;

    public ObservableCollection<NoteListItem> Notes { get; } = new();

    public ObservableCollection<NoteAttachmentItem> Attachments { get; } = new();

    public IReadOnlyList<NoteSortOrder> SortOptions { get; } = Enum.GetValues<NoteSortOrder>();

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
    public partial NoteSortOrder SelectedSort { get; set; } = NoteSortOrder.UpdatedDescending;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool IsEmpty => !IsBusy && Notes.Count == 0;

    public bool HasError => ErrorMessage.Length > 0;

    public string ListStateKey => NoteService.CreateListStateKey(Filter, SelectedSort);

    public NoteDocument? CurrentNote => currentNote;

    public string CurrentRichText => currentNote?.RichText ?? string.Empty;

    public double CurrentEditorScrollOffset => currentNote?.EditorScrollOffset ?? 0;

    partial void OnTitleChanged(string value)
    {
        if (!loadingEditor && currentNote is not null)
        {
            currentNote.Title = value;
            ScheduleSave();
        }
    }

    partial void OnFilterChanged(string value) => _ = ReloadListAsync();

    partial void OnSelectedSortChanged(NoteSortOrder value) => _ = ReloadListAsync();

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
            OnPropertyChanged(nameof(CurrentRichText));
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
            OnPropertyChanged(nameof(CurrentRichText));
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

    public void UpdateDocument(string richText, string plainText)
    {
        if (loadingEditor || currentNote is null)
        {
            return;
        }

        currentNote.RichText = richText;
        currentNote.PlainText = plainText.TrimEnd('\r', '\n');
        ScheduleSave();
    }

    public void UpdateEditorScrollOffset(double offset)
    {
        if (loadingEditor || currentNote is null || Math.Abs(currentNote.EditorScrollOffset - offset) < 0.5)
        {
            return;
        }

        currentNote.EditorScrollOffset = Math.Max(0, offset);
        ScheduleSave();
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
            await NoteService.SaveAsync(note, removed);
            removedAttachmentIds.ExceptWith(removed);
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

using BenchBook.App.Services;
using BenchBook.Core.Capture;
using BenchBook.Core.Store;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BenchBook.App.Views;

/// <summary>"New Note" or "Edit Note" (screens.md, "Note editor").</summary>
public sealed partial class NoteEditorDialog : ContentDialog
{
    private readonly AppServices _services;
    private readonly string _projectId;
    private readonly ReferenceEntry? _entry;

    public NoteEditorDialog(AppServices services, string projectId, ReferenceEntry? entry)
    {
        _services = services;
        _projectId = projectId;
        _entry = entry;
        InitializeComponent();
        Title = entry is null ? "New Note" : "Edit Note";
        PrimaryButtonText = entry is null ? "Add Note" : "Save";
        if (entry is not null)
        {
            TitleBox.Text = entry.Reference.Title ?? "";
            TextBox.Text = entry.Reference.Text ?? "";
            TagsBox.Text = string.Join(", ", entry.Tags.Select(t => t.Name));
        }
        UpdateCanSave();
        Opened += (_, _) => (entry is null ? TextBox : TitleBox).Focus(FocusState.Programmatic);
    }

    private void Changed(object sender, TextChangedEventArgs e) => UpdateCanSave();

    private void UpdateCanSave() =>
        IsPrimaryButtonEnabled = TitleBox.Text.Trim().Length > 0 || TextBox.Text.Trim().Length > 0;

    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // A TextBox gives line breaks as \r; store them as \n, as the Mac does.
        var text = TextBox.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        try
        {
            _services.WithStore(store =>
            {
                if (_entry is null) ReferenceService.CreateNote(store, _projectId, TitleBox.Text, text, TagsBox.Text);
                else ReferenceService.UpdateNote(store, _entry.Reference, TitleBox.Text, text, TagsBox.Text);
                return 0;
            });
        }
        catch (Exception error) when (error is CaptureException or SqliteException)
        {
            args.Cancel = true;
            ErrorText.Text = $"Couldn't Save Note. {error.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}

using BenchBook.App.Services;
using BenchBook.Core.Model;
using BenchBook.Core.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BenchBook.App.Views;

/// <summary>"New Project" or "Edit Project" (screens.md, "Project editor"). Saves only on Create or Save.</summary>
public sealed partial class ProjectEditorDialog : ContentDialog
{
    private readonly AppServices _services;
    private readonly Project? _project;

    public ProjectEditorDialog(AppServices services, Project? project)
    {
        _services = services;
        _project = project;
        InitializeComponent();

        Title = project is null ? "New Project" : "Edit Project";
        PrimaryButtonText = project is null ? "Create" : "Save";

        foreach (var status in ProjectStatus.All)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new FontIcon { Glyph = Words.StatusGlyph(status), FontSize = 14 });
            row.Children.Add(new TextBlock { Text = Words.StatusName(status) });
            StatusBox.Items.Add(row);
        }

        var draft = project is null
            ? new ProjectDraft { Status = services.Settings.NewProjectStatus }
            : ProjectDraft.From(project);
        NameBox.Text = draft.Name;
        ManufacturerBox.Text = draft.Manufacturer;
        SubjectBox.Text = draft.Subject;
        ScaleBox.Text = draft.Scale;
        KitNumberBox.Text = draft.KitNumber;
        MarkingsBox.Text = draft.MarkingsScheme;
        NotesBox.Text = draft.Notes;
        // A status this version doesn't know shows as Researching, but is kept unless changed.
        _unknownStatus = ProjectStatus.All.Contains(draft.Status) ? null : draft.Status;
        StatusBox.SelectedIndex = Math.Max(0, ProjectStatus.All.ToList().IndexOf(draft.Status));
        StatusBox.SelectionChanged += (_, _) => _statusTouched = true;
        UpdateCanSave();

        Opened += (_, _) => NameBox.Focus(FocusState.Programmatic);
    }

    private readonly string? _unknownStatus;
    private bool _statusTouched;

    /// <summary>The project saved, once the dialog closes with Create or Save.</summary>
    internal Project? Saved { get; private set; }

    private ProjectDraft Draft => new()
    {
        Name = NameBox.Text,
        Manufacturer = ManufacturerBox.Text,
        Subject = SubjectBox.Text,
        Scale = ScaleBox.Text,
        KitNumber = KitNumberBox.Text,
        MarkingsScheme = MarkingsBox.Text,
        Notes = NotesBox.Text,
        Status = _unknownStatus is not null && !_statusTouched
            ? _unknownStatus
            : ProjectStatus.All[Math.Max(0, StatusBox.SelectedIndex)],
    };

    private void Name_TextChanged(object sender, TextChangedEventArgs e) => UpdateCanSave();

    private void UpdateCanSave()
    {
        var valid = Draft.HasValidName;
        IsPrimaryButtonEnabled = valid;
        NameHint.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var draft = Draft;
        try
        {
            Saved = _services.WithStore(store => _project is null
                ? ProjectService.Create(store, draft)
                : ProjectService.Update(store, _project, draft));
        }
        catch (Exception error) when (error is ProjectException or SqliteException)
        {
            args.Cancel = true;
            NameHint.Text = $"Couldn't Save Project. {error.Message}";
            NameHint.Visibility = Visibility.Visible;
        }
    }
}

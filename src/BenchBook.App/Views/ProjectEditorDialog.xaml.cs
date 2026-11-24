using BenchBook.App.Services;
using BenchBook.Core.Kits;
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

    /// <param name="name">For a new project from a kit: its starting name.</param>
    /// <param name="kits">For a new project: the kits it starts with.</param>
    public ProjectEditorDialog(AppServices services, Project? project, string? name = null, IEnumerable<Kit>? kits = null)
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
        NameBox.Text = name ?? draft.Name;
        SubjectBox.Text = draft.Subject;
        MarkingsBox.Text = draft.MarkingsScheme;
        NotesBox.Text = draft.Notes;
        // A status this version doesn't know shows as Researching, but is kept unless changed.
        _unknownStatus = ProjectStatus.All.Contains(draft.Status) ? null : draft.Status;
        StatusBox.SelectedIndex = Math.Max(0, ProjectStatus.All.ToList().IndexOf(draft.Status));
        StatusBox.SelectionChanged += (_, _) => _statusTouched = true;
        UpdateCanSave();

        // A new project also chooses its kits; an existing one changes them in its Kits tab.
        if (project is null)
        {
            _kits.AddRange(kits ?? []);
            KitsPanel.Visibility = Visibility.Visible;
            ShowKits();
        }
        Opened += (_, _) => NameBox.Focus(FocusState.Programmatic);
    }

    private readonly List<Kit> _kits = [];
    private Func<Task>? _next;

    /// <summary>
    /// Shows the editor until it's saved or cancelled. Add from Kits… and New
    /// Kit… close it (only one dialog can be open) and it comes back after.
    /// </summary>
    internal async Task<Project?> RunAsync()
    {
        while (true)
        {
            _next = null;
            var result = await ShowAsync();
            if (result == ContentDialogResult.Primary) return Saved;
            if (_next is null) return null;
            await _next();
            ShowKits();
        }
    }

    private void ShowKits()
    {
        KitsPanel.Children.Clear();
        KitsPanel.Children.Add(new TextBlock { Text = "Kits", Margin = new Thickness(0, 6, 0, 0) });
        foreach (var kit in _kits)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var picture = KitViews.Picture(_services, kit.PictureAssetId, 32);
            row.Children.Add(picture);
            var text = TableRows.TwoLines(kit.Name, KitText.Details(kit));
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(6) };
            ToolTipService.SetToolTip(remove, "Remove from this project");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"Remove {KitText.DisplayName(kit)}");
            remove.Click += (_, _) => { _kits.Remove(kit); ShowKits(); };
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            KitsPanel.Children.Add(row);
        }
        var add = new Button { Content = "Add from Kits…" };
        add.Click += (_, _) =>
        {
            _next = async () =>
            {
                var picker = new KitPickerDialog(_services, NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : "New Project", _kits.Select(k => k.Id)) { XamlRoot = XamlRoot };
                _kits.AddRange(await picker.PickAsync());
            };
            Hide();
        };
        var create = new Button { Content = "New Kit…" };
        create.Click += (_, _) =>
        {
            _next = async () =>
            {
                if (await KitViews.Edit(XamlRoot, _services, null) is { } kit) _kits.Add(kit);
            };
            Hide();
        };
        KitsPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, create } });
        KitsPanel.Children.Add(new TextBlock
        {
            Text = "New kits go into your Kits catalogue straight away, even if you cancel.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
    }

    private readonly string? _unknownStatus;
    private bool _statusTouched;

    /// <summary>The project saved, once the dialog closes with Create or Save.</summary>
    internal Project? Saved { get; private set; }

    private ProjectDraft Draft => new()
    {
        Name = NameBox.Text,
        Subject = SubjectBox.Text,
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

    /// <summary>The chosen kits as they are now. A kit another project took meanwhile moves to this one.</summary>
    private List<Kit> KitsStillFree(BenchBook.Core.Store.LibraryStore store) =>
        _kits.Select(k => store.Kit(k.Id)).OfType<Kit>().ToList();

    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var draft = Draft;
        try
        {
            Saved = _services.WithStore(store => _project is null
                ? ProjectService.Create(store, draft, KitsStillFree(store))
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

using BenchBook.App.Services;
using BenchBook.App.Views;
using BenchBook.Core.Backup;
using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Projects;
using BenchBook.Core.Search;
using BenchBook.Core.Store;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace BenchBook.App;

public sealed partial class MainWindow : Window
{
    private readonly AppServices _services;
    private RestoreOutcome? _outcome;
    private List<Project> _projects = [];
    private bool _rebuilding;
    private bool _showAllTags;
    private LibrarySearch? _searchIndex;
    private SearchPage? _searchPage;
    private readonly List<Tag> _searchTags = [];

    /// <summary>A tag offered as a search token while typing.</summary>
    private sealed record TagSuggestion(Tag Tag)
    {
        public override string ToString() => $"Tag: {Tag.Name}";
    }
    private readonly AutoSuggestBox _search = new() { PlaceholderText = "Search everything", QueryIcon = new SymbolIcon(Symbol.Find) };
    private readonly List<object> _tagItems = [];
    private const int CollapsedTagCount = 10;

    /// <summary>A tag in the sidebar.</summary>
    private sealed record TagItem(string Id);

    public MainWindow(AppServices services, RestoreOutcome? outcome)
    {
        _services = services;
        _outcome = outcome;
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));
        AppWindow.SetIcon("Assets/Square44x44Logo.png");
        BuildSidebar(select: null);
        // Tag counts change as references are tagged, so the Tags section follows the library.
        services.LibraryChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            _searchIndex = null;
            UpdateTags();
        });
        services.ShowProject = ShowProject;

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_search, "Search everything");
        _search.TextChanged += Search_TextChanged;
        _search.UpdateTextOnSelect = false;
        _search.QuerySubmitted += (_, args) =>
        {
            if (args.ChosenSuggestion is TagSuggestion suggestion) AddSearchTag(suggestion.Tag);
            else ShowSearch();
        };
        Navigation.AutoSuggestBox = _search;
        var find = new KeyboardAccelerator { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.F };
        find.Invoked += (_, args) =>
        {
            args.Handled = true;
            _search.Focus(FocusState.Keyboard);
        };
        Navigation.KeyboardAccelerators.Add(find);
    }

    private string? StatusFilter => _services.Settings.ProjectStatusFilter;

    // MARK: Sidebar

    /// <summary>Lists the projects, filtered by status, then the library. Selects the given project if it's shown.</summary>
    private void BuildSidebar(string? select)
    {
        _projects = _services.WithStore(s => s.Projects());
        _projects.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
        var visible = _projects.Where(p => StatusFilter is null || (p.Status ?? ProjectStatus.Researching) == StatusFilter).ToList();

        _rebuilding = true;
        Navigation.MenuItems.Clear();
        // A NavigationViewItemHeader shows only text, so the title and its buttons sit in the pane's header row.
        Navigation.PaneHeader = ProjectsHeader();

        NavigationViewItem? selected = null;
        foreach (var project in visible)
        {
            var item = ProjectItem(project);
            if (select is not null && Ids.Comparer.Equals(project.Id, select)) selected = item;
            Navigation.MenuItems.Add(item);
        }
        if (visible.Count == 0 && _projects.Count > 0 && StatusFilter is not null)
            Navigation.MenuItems.Add(NoMatchingProjects());

        Navigation.MenuItems.Add(new NavigationViewItemHeader { Content = "Library" });
        Navigation.MenuItems.Add(Item("Paints", "\uE790", "paints"));
        Navigation.MenuItems.Add(Item("Shopping List", "\uE7BF", "shopping"));
        Navigation.MenuItems.Add(Item("Tags", "\uE8EC", "tags"));
        _tagItems.Clear();
        AddTagItems();
        _rebuilding = false;

        if (selected is not null) Navigation.SelectedItem = selected;
        else if (Navigation.SelectedItem is null || !ReferenceEquals(Navigation.SelectedItem, Navigation.SettingsItem))
            ShowStart();
    }

    /// <summary>"Projects" or "Projects · In Progress", with the status filter and New Project.</summary>
    private Grid ProjectsHeader()
    {
        var grid = new Grid { ColumnSpacing = 2, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = StatusFilter is null ? "Projects" : $"Projects · {Words.StatusName(StatusFilter)}",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 0, 0),
        };
        grid.Children.Add(title);

        var menu = new MenuFlyout();
        var all = new RadioMenuFlyoutItem { Text = $"All Projects ({_projects.Count})", GroupName = "status", IsChecked = StatusFilter is null };
        all.Click += (_, _) => SetStatusFilter(null);
        menu.Items.Add(all);
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var status in ProjectStatus.All)
        {
            var count = _projects.Count(p => (p.Status ?? ProjectStatus.Researching) == status);
            var option = new RadioMenuFlyoutItem
            {
                Text = $"{Words.StatusName(status)} ({count})",
                GroupName = "status",
                IsChecked = StatusFilter == status,
                Icon = new FontIcon { Glyph = Words.StatusGlyph(status) },
            };
            option.Click += (_, _) => SetStatusFilter(status);
            menu.Items.Add(option);
        }
        var filter = SmallButton(StatusFilter is null ? "\uE71C" : "\uE16E", "Show projects by status");
        AutomationName(filter, "Filter Projects by Status");
        filter.Flyout = menu;
        Grid.SetColumn(filter, 1);
        grid.Children.Add(filter);

        var add = SmallButton("\uE710", "New Project (Ctrl+N)");
        AutomationName(add, "New Project");
        add.Click += async (_, _) => await NewProject();
        Grid.SetColumn(add, 2);
        grid.Children.Add(add);
        return grid;
    }

    private static Button SmallButton(string glyph, string tip)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Padding = new Thickness(6),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, tip);
        return button;
    }

    private static void AutomationName(UIElement element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);

    private NavigationViewItem ProjectItem(Project project)
    {
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 4, 0, 4) };
        text.Children.Add(new TextBlock { Text = project.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        var summary = Words.ProjectSummary(project);
        if (summary.Length > 0)
            text.Children.Add(new TextBlock
            {
                Text = summary,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        content.Children.Add(text);
        var status = new FontIcon
        {
            Glyph = Words.StatusGlyph(project.Status),
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        ToolTipService.SetToolTip(status, Words.StatusName(project.Status));
        Grid.SetColumn(status, 1);
        content.Children.Add(status);

        var item = new NavigationViewItem { Content = content, Tag = project, AllowDrop = true };
        // References dropped here move to the project; holding Ctrl copies them, as in Explorer.
        item.DragOver += (_, e) =>
        {
            // Files, links or text dropped on a project are added to it, without selecting it.
            if (CaptureReader.CanCapture(e.DataView))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.DragUIOverride.Caption = $"Add to {project.Name}";
                return;
            }
            if (!e.DataView.Contains(AppServices.ReferencesDragFormat)) return;
            var copy = e.Modifiers.HasFlag(Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control);
            e.AcceptedOperation = copy ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy : Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            e.DragUIOverride.Caption = copy ? $"Copy to {project.Name}" : $"Move to {project.Name}";
        };
        item.Drop += async (_, e) =>
        {
            if (CaptureReader.CanCapture(e.DataView))
            {
                var deferral = e.GetDeferral();
                try
                {
                    var report = await CaptureReader.CaptureAsync(_services, e.DataView, project.Id);
                    _services.NotifyLibraryChanged();
                    if (report is not null) await ShowMessage(report.Title, report.Message);
                }
                finally
                {
                    deferral.Complete();
                }
                return;
            }
            if (await DraggedReferences(e) is not { } ids) return;
            var mode = e.Modifiers.HasFlag(Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control) ? TransferMode.Copy : TransferMode.Move;
            await DropOnProject(ids, project, mode);
        };
        AutomationName(item, string.Join(", ", new[] { project.Name, summary, Words.StatusName(project.Status) }.Where(s => s.Length > 0)));

        var menu = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = "Edit Project…", Icon = new SymbolIcon(Symbol.Edit) };
        edit.Click += async (_, _) => await EditProject(project);
        menu.Items.Add(edit);
        var statusMenu = new MenuFlyoutSubItem { Text = "Status" };
        foreach (var option in ProjectStatus.All)
        {
            var choice = new RadioMenuFlyoutItem
            {
                Text = Words.StatusName(option),
                GroupName = "projectStatus",
                IsChecked = (project.Status ?? ProjectStatus.Researching) == option,
                Icon = new FontIcon { Glyph = Words.StatusGlyph(option) },
            };
            choice.Click += async (_, _) => await SetStatus(project, option);
            statusMenu.Items.Add(choice);
        }
        menu.Items.Add(statusMenu);
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = "Delete Project…", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await DeleteProject(project);
        menu.Items.Add(delete);
        item.ContextFlyout = menu;
        return item;
    }

    /// <summary>"No In Progress Projects", with "Show All Projects".</summary>
    private NavigationViewItem NoMatchingProjects()
    {
        var panel = new StackPanel { Spacing = 4, Padding = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(new TextBlock
        {
            Text = $"No {Words.StatusName(StatusFilter)} Projects",
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var showAll = new HyperlinkButton { Content = "Show All Projects", Padding = new Thickness(0) };
        showAll.Click += (_, _) => SetStatusFilter(null);
        panel.Children.Add(showAll);
        return new NavigationViewItem { Content = panel, SelectsOnInvoked = false, IsTabStop = false };
    }

    // MARK: Search

    private LibrarySearch SearchIndex() => _searchIndex ??= _services.WithStore(s => new LibrarySearch(s));

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        sender.ItemsSource = TagSuggestions(sender.Text);
        ShowSearch();
    }

    /// <summary>Tags whose name contains the word being typed, offered as tokens.</summary>
    private List<TagSuggestion> TagSuggestions(string text)
    {
        var word = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (word is null || char.IsWhiteSpace(text[^1])) return [];
        var folded = SearchField.Fold(word);
        return _services.WithStore(s => s.TagsByUsage())
            .Where(u => u.References > 0 && !_searchTags.Any(t => Ids.Comparer.Equals(t.Id, u.Tag.Id)) && SearchField.Fold(u.Tag.Name).Contains(folded, StringComparison.Ordinal))
            .Take(6).Select(u => new TagSuggestion(u.Tag)).ToList();
    }

    /// <summary>Turns the word being typed into a tag token.</summary>
    private void AddSearchTag(Tag tag)
    {
        if (!_searchTags.Any(t => Ids.Comparer.Equals(t.Id, tag.Id))) _searchTags.Add(tag);
        var text = _search.Text.TrimEnd();
        var space = text.LastIndexOfAny([' ', '\t']);
        _search.Text = space < 0 ? "" : text[..(space + 1)];
        _search.ItemsSource = null;
        ShowSearch();
    }

    private void RemoveSearchTag(string tagId)
    {
        _searchTags.RemoveAll(t => Ids.Comparer.Equals(t.Id, tagId));
        ShowSearch();
    }

    /// <summary>Shows results in place of the page; with no words and no tags, goes back to what was selected.</summary>
    private void ShowSearch()
    {
        var text = _search.Text;
        if (string.IsNullOrWhiteSpace(text) && _searchTags.Count == 0)
        {
            _searchPage = null;
            var selected = Navigation.SelectedItem;
            _rebuilding = true;
            Navigation.SelectedItem = null;
            _rebuilding = false;
            if (selected is not null) Navigation.SelectedItem = selected;
            else ShowStart();
            return;
        }
        if (_searchPage is null || !ReferenceEquals(ContentFrame.Content, _searchPage))
        {
            _searchPage = new SearchPage(_services, SearchIndex, RemoveSearchTag);
            ContentFrame.Content = _searchPage;
        }
        _searchPage.Show(text, _searchTags.ToList());
    }

    /// <summary>Leaves search: the field and the tag tokens are cleared.</summary>
    private void EndSearch()
    {
        _search.Text = "";
        _search.ItemsSource = null;
        _searchTags.Clear();
        _searchPage = null;
    }

    /// <summary>Selects a project in the sidebar, showing all projects if the filter hides it.</summary>
    private void ShowProject(string projectId)
    {
        EndSearch();
        var item = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag is Project p && Ids.Comparer.Equals(p.Id, projectId));
        if (item is null && StatusFilter is not null)
        {
            _services.Settings.ProjectStatusFilter = null;
            _services.SaveSettings();
            BuildSidebar(projectId);
            return;
        }
        if (item is null) return;
        if (ReferenceEquals(Navigation.SelectedItem, item))
        {
            _rebuilding = true;
            Navigation.SelectedItem = null;
            _rebuilding = false;
        }
        Navigation.SelectedItem = item;
    }

    // MARK: Dropping references on the sidebar

    private static async Task<List<string>?> DraggedReferences(DragEventArgs e)
    {
        if (!e.DataView.Contains(AppServices.ReferencesDragFormat)) return null;
        var deferral = e.GetDeferral();
        try
        {
            var text = await e.DataView.GetDataAsync(AppServices.ReferencesDragFormat) as string;
            var ids = (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            return ids.Count > 0 ? ids : null;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task DropOnProject(List<string> ids, Project project, TransferMode mode)
    {
        TransferResult result;
        try
        {
            result = _services.WithStore(s => ReferenceTransfer.Transfer(s, _services.Location, ids, project.Id, mode));
        }
        catch (Exception error) when (error is CaptureException or Microsoft.Data.Sqlite.SqliteException)
        {
            await ShowMessage(mode == TransferMode.Move ? "Couldn't Move" : "Couldn't Copy", error.Message);
            return;
        }
        if (result.Transferred.Count == 0 && result.Duplicates.Count == 0) return;
        _services.NotifyLibraryChanged();
        _services.NoticeTransfer(result.Notice(mode, project.Name), project.Id, result.Transferred.Count > 0);
    }

    // MARK: Tags in the sidebar

    /// <summary>The ten most used tags with their colour and count, then Show More or Show Less.</summary>
    private void AddTagItems()
    {
        var tags = _services.WithStore(s => s.TagsByUsage());
        if (tags.Count == 0) return;
        var items = new List<object> { new NavigationViewItemHeader { Content = "Tags" } };
        foreach (var usage in _showAllTags ? tags : tags.Take(CollapsedTagCount))
            items.Add(TagNavigationItem(usage));
        if (tags.Count > CollapsedTagCount)
        {
            var more = new HyperlinkButton
            {
                Content = _showAllTags ? "Show Less" : $"Show More ({tags.Count - CollapsedTagCount:N0})",
                Padding = new Thickness(0),
            };
            more.Click += (_, _) =>
            {
                _showAllTags = !_showAllTags;
                UpdateTags();
            };
            items.Add(new NavigationViewItem { Content = more, SelectsOnInvoked = false, IsTabStop = false });
        }
        foreach (var item in items) Navigation.MenuItems.Add(item);
        _tagItems.AddRange(items);
    }

    private NavigationViewItem TagNavigationItem(TagUsage usage)
    {
        var tag = usage.Tag;
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = TagCommands.Dot(tag.Color);
        dot.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(dot);
        var name = new TextBlock { Text = tag.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        content.Children.Add(name);
        var count = new TextBlock
        {
            Text = usage.References.ToString("N0"),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(count, 2);
        content.Children.Add(count);

        var item = new NavigationViewItem { Content = content, Tag = new TagItem(tag.Id), AllowDrop = true, CanDrag = true };
        // A tag dragged onto references in the gallery tags them.
        item.DragStarting += (_, e) =>
        {
            e.Data.SetData(AppServices.TagDragFormat, tag.Id);
            e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        };
        // References dropped here get the tag.
        item.DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(AppServices.ReferencesDragFormat)) return;
            // Must be one the gallery offers (move or copy), or Windows refuses the drop.
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = $"Tag with “{tag.Name}”";
        };
        item.Drop += async (_, e) =>
        {
            if (await DraggedReferences(e) is not { } ids) return;
            try
            {
                _services.WithStore(s => { s.AddTag(tag.Id, ids); return 0; });
            }
            catch (Microsoft.Data.Sqlite.SqliteException error)
            {
                await ShowMessage("Couldn't Change Tags", error.Message);
                return;
            }
            _services.NotifyLibraryChanged();
        };
        AutomationName(item, $"{tag.Name}, {usage.References:N0} {(usage.References == 1 ? "reference" : "references")}");

        var menu = new MenuFlyout();
        var show = new MenuFlyoutItem { Text = "Show References" };
        show.Click += (_, _) => ShowTag(tag.Id);
        menu.Items.Add(show);
        var rename = new MenuFlyoutItem { Text = "Rename…" };
        rename.Click += async (_, _) => { if (await TagCommands.Rename(Content.XamlRoot, _services, tag)) TagsChanged(); };
        menu.Items.Add(rename);
        menu.Items.Add(TagCommands.ColourMenu(_services, new[] { tag }, () => { TagsChanged(); return Task.CompletedTask; }));
        var manage = new MenuFlyoutItem { Text = "Manage Tags…" };
        manage.Click += (_, _) => SelectLibraryItem("tags");
        menu.Items.Add(manage);
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = "Delete Tag…", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => { if (await TagCommands.Delete(Content.XamlRoot, _services, new[] { usage })) TagsChanged(); };
        menu.Items.Add(delete);
        item.ContextFlyout = menu;
        return item;
    }

    /// <summary>Rebuilds only the Tags section, so the project on screen keeps its state.</summary>
    private void UpdateTags()
    {
        var selected = (Navigation.SelectedItem as NavigationViewItem)?.Tag as TagItem;
        _rebuilding = true;
        foreach (var item in _tagItems) Navigation.MenuItems.Remove(item);
        _tagItems.Clear();
        AddTagItems();
        if (selected is not null)
            Navigation.SelectedItem = _tagItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag is TagItem t && Ids.Comparer.Equals(t.Id, selected.Id));
        _rebuilding = false;
    }

    /// <summary>After a tag change made from the sidebar: refresh what's on screen too.</summary>
    private void TagsChanged()
    {
        _services.NotifyLibraryChanged();
        switch ((Navigation.SelectedItem as NavigationViewItem)?.Tag)
        {
            case Project project:
                ContentFrame.Navigate(typeof(ProjectPage), new ProjectPageArgs(_services, project, EditProject, DeleteProject), new SuppressNavigationTransitionInfo());
                break;
            case TagItem tag:
                ContentFrame.Content = new TagReferencesPage(new TagPageArgs(_services, tag.Id));
                break;
            case "tags":
                ContentFrame.Content = new TagsPage(_services, ShowTag);
                break;
        }
    }

    /// <summary>Shows every reference with the tag, selecting it in the sidebar when it's listed there.</summary>
    private void ShowTag(string tagId)
    {
        var item = _tagItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag is TagItem t && Ids.Comparer.Equals(t.Id, tagId));
        if (item is not null) Navigation.SelectedItem = item;
        else ContentFrame.Content = new TagReferencesPage(new TagPageArgs(_services, tagId));
    }

    private void SelectLibraryItem(string tag) =>
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == tag);

    private static NavigationViewItem Item(string title, string glyph, string tag) =>
        new() { Content = title, Tag = tag, Icon = new FontIcon { Glyph = glyph } };

    private void SetStatusFilter(string? status)
    {
        _services.Settings.ProjectStatusFilter = status;
        _services.SaveSettings();
        var current = (Navigation.SelectedItem as NavigationViewItem)?.Tag as Project;
        BuildSidebar(current?.Id);
    }

    private void ShowStart()
    {
        var message = _projects.Count == 0
            ? new Placeholder("No Projects", "Create a project for each build to keep its references, paint and aftermarket parts together.", "Choose + beside Projects, or press Ctrl+N.")
            : new Placeholder("No Project Selected", "Select a project in the sidebar.");
        ContentFrame.Navigate(typeof(PlaceholderPage), message, new SuppressNavigationTransitionInfo());
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_rebuilding) return;
        // Choosing something in the sidebar leaves search.
        if (_search.Text.Length > 0 || _searchTags.Count > 0) EndSearch();
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage), _services, new SuppressNavigationTransitionInfo());
            return;
        }
        switch ((args.SelectedItem as NavigationViewItem)?.Tag)
        {
            case Project project:
                ContentFrame.Navigate(typeof(ProjectPage), new ProjectPageArgs(_services, project, EditProject, DeleteProject), new SuppressNavigationTransitionInfo());
                break;
            case "paints":
                ShowPlaceholder(new Placeholder("Paints", "The paint catalogue arrives in a later step."));
                break;
            case "shopping":
                ShowPlaceholder(new Placeholder("Shopping List", "The shopping list arrives in a later step."));
                break;
            case "tags":
                ContentFrame.Content = new TagsPage(_services, ShowTag);
                break;
            case TagItem tag:
                ContentFrame.Content = new TagReferencesPage(new TagPageArgs(_services, tag.Id));
                break;
        }
    }

    private void ShowPlaceholder(Placeholder placeholder) =>
        ContentFrame.Navigate(typeof(PlaceholderPage), placeholder, new SuppressNavigationTransitionInfo());

    // MARK: Project commands

    private async void NewProject_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await NewProject();
    }

    private async Task NewProject()
    {
        var dialog = new ProjectEditorDialog(_services, null) { XamlRoot = Content.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Saved is not { } project) return;
        // A new project that the filter would hide shows all projects instead.
        if (StatusFilter is not null && project.Status != StatusFilter)
        {
            _services.Settings.ProjectStatusFilter = null;
            _services.SaveSettings();
        }
        BuildSidebar(project.Id);
    }

    private async Task EditProject(Project project)
    {
        var dialog = new ProjectEditorDialog(_services, project) { XamlRoot = Content.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Saved is not { } saved) return;
        BuildSidebar(saved.Id);
        if (Navigation.SelectedItem is NavigationViewItem { Tag: Project shown } && shown.Id == saved.Id)
            ContentFrame.Navigate(typeof(ProjectPage), new ProjectPageArgs(_services, saved, EditProject, DeleteProject), new SuppressNavigationTransitionInfo());
    }

    private async Task SetStatus(Project project, string status)
    {
        try
        {
            var saved = _services.WithStore(s => ProjectService.SetStatus(s, project, status));
            var wasShown = Navigation.SelectedItem is NavigationViewItem { Tag: Project shown } && shown.Id == saved.Id;
            BuildSidebar(saved.Id);
            if (wasShown)
                ContentFrame.Navigate(typeof(ProjectPage), new ProjectPageArgs(_services, saved, EditProject, DeleteProject), new SuppressNavigationTransitionInfo());
        }
        catch (Exception error) when (error is ProjectException or SqliteException)
        {
            await ShowMessage("Couldn't Save Project", error.Message);
        }
    }

    private async Task DeleteProject(Project project)
    {
        var contents = _services.WithStore(s => s.ProjectContents(project.Id));
        var confirm = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Delete “{project.Name}”?",
            Content = Wrapped(ProjectService.DeletionMessage(contents)),
            PrimaryButtonText = "Delete Project",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _services.WithStoreAsync(s => { ProjectService.Delete(s, _services.Location, project); return 0; });
        }
        catch (SqliteException error)
        {
            await ShowMessage("Couldn't Delete Project", error.Message);
            return;
        }
        BuildSidebar(select: null);
        ShowStart();
    }

    // MARK: Messages

    /// <summary>Tells the user how a restore finished, once.</summary>
    private async void Navigation_Loaded(object sender, RoutedEventArgs e)
    {
        if (_outcome is not { } outcome) return;
        _outcome = null;

        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close };
        switch (outcome)
        {
            case RestoreOutcome.Restored restored:
                dialog.Title = "Library Restored";
                dialog.Content = Wrapped(restored.PreviousLibrary is { } previous
                    ? $"Your library is back as it was on {Words.DateAndTime(restored.BackupDate)}. The library you had before is kept in “{Path.GetFileName(previous)}”, also listed in Settings › Backup."
                    : $"Your library is back as it was on {Words.DateAndTime(restored.BackupDate)}.");
                if (restored.PreviousLibrary is not null) dialog.PrimaryButtonText = "Show Earlier Library";
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && restored.PreviousLibrary is { } folder)
                    Shell.ShowInExplorer(folder);
                break;
            case RestoreOutcome.Failed failed:
                dialog.Title = "Restore Didn't Finish";
                dialog.Content = Wrapped(failed.Message);
                await dialog.ShowAsync();
                break;
        }
    }

    private async Task ShowMessage(string title, string message)
    {
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = title, Content = Wrapped(message), CloseButtonText = "OK" };
        await dialog.ShowAsync();
    }

    private static TextBlock Wrapped(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
}

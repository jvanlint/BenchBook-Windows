using BenchBook.App.Services;
using BenchBook.Core.Kits;
using BenchBook.Core.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>A project's Kits tab (screens.md, "Project Kits tab"): the kits this build uses.</summary>
internal sealed partial class ProjectKitsView : UserControl
{
    private static readonly string[] Widths = ["48", "2*", "80", "130", "44"];

    private readonly AppServices _services;
    private readonly Project _project;
    private readonly Action _changed;
    private readonly TextBlock _summary = new() { Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
    private readonly ListView _list;
    private readonly FrameworkElement _header;
    private readonly StackPanel _top;
    private readonly StackPanel _empty = new() { Spacing = 10, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    /// <param name="changed">Called after the project's kits change, so the header's summary follows.</param>
    public ProjectKitsView(AppServices services, Project project, Action changed)
    {
        _services = services;
        _project = project;
        _changed = changed;
        _list = TableRows.List(FillRow);
        _list.DoubleTapped += async (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is Kit kit) await Edit(kit);
        };
        _list.ContextRequested += List_ContextRequested;
        // Delete removes from the project without asking; the kits stay in the catalogue.
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Delete && Selected().Count > 0) { e.Handled = true; Remove(Selected()); }
        };
        _header = TableRows.Header(Widths, [("Picture", null), ("Kit", null), ("Scale", null), ("Status", null), ("Link", null)], null, true, null);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(_summary);
        _top = Buttons();
        Grid.SetColumn(_top, 1);
        top.Children.Add(_top);

        _empty.Children.Add(new TextBlock { Text = "No Kits", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(new TextBlock
        {
            Text = "Choose the kits for this build from your Kits catalogue, or add a new one.",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var emptyButtons = Buttons();
        emptyButtons.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Children.Add(emptyButtons);

        var root = new Grid { RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(top);
        Grid.SetRow(_header, 1);
        root.Children.Add(_header);
        Grid.SetRow(_list, 2);
        root.Children.Add(_list);
        Grid.SetRow(_empty, 2);
        root.Children.Add(_empty);
        Content = root;
        Load();
    }

    private StackPanel Buttons()
    {
        var add = new Button { Content = "Add from Kits…" };
        ToolTipService.SetToolTip(add, "Choose kits for this build from your Kits catalogue");
        add.Click += async (_, _) => await AddFromKits();
        var create = new Button { Content = "New Kit…" };
        ToolTipService.SetToolTip(create, "Add a kit to your catalogue and to this build");
        create.Click += async (_, _) => await Edit(null);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, create } };
    }

    private Project Current => _services.WithStore(s => s.Project(_project.Id)) ?? _project;

    private void Load()
    {
        var kits = KitText.Sorted(_services.WithStore(s => s.ProjectKits(_project.Id)));
        _list.ItemsSource = kits;
        var notOwned = kits.Count(k => !k.Owned);
        _summary.Text = notOwned > 0 ? $"{KitText.Count(kits.Count)} · {notOwned:N0} not owned" : KitText.Count(kits.Count);

        var empty = kits.Count == 0;
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _header.Visibility = _list.Visibility;
        _summary.Visibility = _list.Visibility;
        _top.Visibility = _list.Visibility;
    }

    private void Changed()
    {
        Load();
        _changed();
        _services.NotifyLibraryChanged();
    }

    private void FillRow(Grid grid, object value)
    {
        var kit = (Kit)value;
        grid.DataContext = kit;
        TableRows.Columns(grid, Widths);
        TableRows.Cell(grid, KitViews.Picture(_services, kit.PictureAssetId), 0);
        var name = TableRows.Cell(grid, TableRows.TwoLines(kit.Name, KitText.BrandAndNumber(kit)), 1);
        if (kit.Notes is { } notes) ToolTipService.SetToolTip(name, notes);
        TableRows.Cell(grid, TableRows.Text(kit.Scale ?? "", secondary: true), 2);
        TableRows.Cell(grid, KitViews.StatusCell(_services, kit, Changed), 3);
        if (KitViews.LinkButton(kit) is { } link) TableRows.Cell(grid, link, 4);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, $"{KitText.DisplayName(kit)}, {KitText.StatusOrNotOwned(kit)}");
    }

    private List<Kit> Selected() => _list.SelectedItems.OfType<Kit>().ToList();

    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var element = args.OriginalSource as FrameworkElement;
        while (element is not null && element.DataContext is not Kit) element = VisualTreeHelper.GetParent(element) as FrameworkElement;
        if (element?.DataContext is not Kit clicked) return;
        args.Handled = true;
        if (!_list.SelectedItems.Contains(clicked))
        {
            _list.SelectedItems.Clear();
            _list.SelectedItems.Add(clicked);
        }
        var chosen = Selected();
        var menu = new MenuFlyout();
        if (chosen.Count == 1)
        {
            var edit = new MenuFlyoutItem { Text = "Edit Kit…", Icon = new SymbolIcon(Symbol.Edit) };
            edit.Click += async (_, _) => await Edit(clicked);
            menu.Items.Add(edit);
            var show = new MenuFlyoutItem { Text = "Show in Kits" };
            show.Click += (_, _) => _services.ShowKit?.Invoke(clicked.Id);
            menu.Items.Add(show);
            if (clicked.Link is { } link)
            {
                var open = new MenuFlyoutItem { Text = "Open Link", Icon = new SymbolIcon(Symbol.World) };
                open.Click += async (_, _) => await Opener.OpenLink(link);
                menu.Items.Add(open);
            }
        }
        if (chosen.Any(k => k.Owned)) menu.Items.Add(KitViews.StatusMenu(_services, chosen, Changed));
        menu.Items.Add(new MenuFlyoutSeparator());
        var remove = new MenuFlyoutItem { Text = chosen.Count == 1 ? "Remove from Project" : $"Remove {chosen.Count:N0} Kits from Project" };
        remove.Click += (_, _) => Remove(chosen);
        menu.Items.Add(remove);
        if (args.TryGetPosition(_list, out var point)) menu.ShowAt(_list, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(element);
    }

    public async Task AddFromKits()
    {
        var picker = new KitPickerDialog(_services, _project.Name) { XamlRoot = XamlRoot };
        var chosen = await picker.PickAsync();
        if (chosen.Count == 0) return;
        _services.WithStore(s => { KitService.Add(s, chosen, Current); return 0; });
        Changed();
    }

    private async Task Edit(Kit? kit)
    {
        if (await KitViews.Edit(XamlRoot, _services, kit, kit is null ? Current : null) is not null) Changed();
    }

    private void Remove(List<Kit> kits)
    {
        _services.WithStore(s => { KitService.Remove(s, kits); return 0; });
        Changed();
    }
}

using BenchBook.App.Services;
using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Store;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>
/// "Tags for “Main Gear and Bay”" or "Tags for 3 References" (screens.md,
/// "Edit tags"). Each add or remove is saved straight away.
/// </summary>
internal sealed class ReferenceTagsDialog : ContentDialog
{
    private readonly AppServices _services;
    private readonly List<string> _referenceIds;
    private readonly StackPanel _onTags = new() { Spacing = 4 };
    private readonly StackPanel _existing = new() { Spacing = 2 };
    private readonly TextBox _field = new() { Header = "Add Tags", PlaceholderText = "Type tags, separated with commas, then press Enter" };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    public ReferenceTagsDialog(AppServices services, IReadOnlyList<ReferenceEntry> entries)
    {
        _services = services;
        _referenceIds = entries.Select(e => e.Reference.Id).ToList();
        Title = entries.Count == 1 ? $"Tags for “{ReferenceText.FallbackTitle(entries[0])}”" : $"Tags for {entries.Count:N0} References";
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;
        _error.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];

        _field.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            AddTyped();
        };
        // Done, or Escape, adds anything still typed.
        Closing += (_, _) => AddTyped();

        var panel = new StackPanel { Spacing = 14, MinWidth = 420 };
        panel.Children.Add(_onTags);
        panel.Children.Add(_field);
        panel.Children.Add(new TextBlock { Text = "Add an existing tag", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        panel.Children.Add(new ScrollViewer { Content = _existing, MaxHeight = 220 });
        panel.Children.Add(_error);
        Content = new ScrollViewer { Content = panel, MaxHeight = 560 };
        Opened += (_, _) => _field.Focus(FocusState.Programmatic);
        Refresh();
    }

    /// <summary>True once anything was changed, so the caller can reload.</summary>
    public bool Changed { get; private set; }

    private void Refresh()
    {
        var entries = _services.WithStore(s => s.ReferenceEntries(_referenceIds));
        var all = _services.WithStore(s => s.TagsByUsage());
        var counts = entries.SelectMany(e => e.Tags).GroupBy(t => t.Id, Ids.Comparer).ToDictionary(g => g.Key, g => g.Count(), Ids.Comparer);

        _onTags.Children.Clear();
        if (counts.Count == 0)
            _onTags.Children.Add(new TextBlock { Text = "No tags yet.", Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        foreach (var usage in all.Where(u => counts.ContainsKey(u.Tag.Id)).OrderBy(u => u.Tag.Name, NaturalComparer.Instance))
        {
            var tag = usage.Tag;
            var partly = counts[tag.Id] < entries.Count;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dot = TagCommands.Dot(tag.Color, 12);
            dot.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(dot);
            var name = new TextBlock { Text = partly ? $"{tag.Name} (on some)" : tag.Name, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (partly)
            {
                var all2 = new Button { Content = "Add to All" };
                ToolTipService.SetToolTip(all2, "On some of these references. Click to add it to all of them.");
                all2.Click += (_, _) => Change(s => s.AddTag(tag.Id, _referenceIds));
                buttons.Children.Add(all2);
            }
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 } };
            ToolTipService.SetToolTip(remove, $"Remove “{tag.Name}”");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"Remove {tag.Name}");
            remove.Click += (_, _) => Change(s => s.RemoveTag(tag.Id, _referenceIds));
            buttons.Children.Add(remove);
            Grid.SetColumn(buttons, 2);
            row.Children.Add(buttons);
            _onTags.Children.Add(row);
        }

        _existing.Children.Clear();
        foreach (var usage in all.Where(u => !counts.TryGetValue(u.Tag.Id, out var n) || n < entries.Count))
        {
            var tag = usage.Tag;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(TagCommands.Dot(tag.Color));
            content.Children.Add(new TextBlock { Text = tag.Name });
            var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            ToolTipService.SetToolTip(button, $"Add “{tag.Name}”");
            button.Click += (_, _) => Change(s => s.AddTag(tag.Id, _referenceIds));
            _existing.Children.Add(button);
        }
    }

    private void AddTyped()
    {
        var text = _field.Text;
        if (TagNames.Parse(text).Count == 0) return;
        _field.Text = "";
        Change(s => s.AddTags(text, _referenceIds));
    }

    private void Change(Action<LibraryStore> change)
    {
        try
        {
            _services.WithStore(s => { change(s); return 0; });
            Changed = true;
            _error.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) when (error is TagException or SqliteException)
        {
            _error.Text = $"Couldn't Change Tags. {error.Message}";
            _error.Visibility = Visibility.Visible;
        }
        Refresh();
    }
}

using BenchBook.App.Services;
using BenchBook.Core.Store;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

internal sealed record TagPageArgs(AppServices Services, string TagId);

/// <summary>Every reference with one tag, grouped by project (screens.md, "Tags").</summary>
internal sealed partial class TagReferencesPage : Page
{
    private readonly TextBlock _title = new() { Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] };
    private readonly TextBlock _subtitle = new() { Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
    private readonly ReferenceGroupsView _groups = new();
    private readonly StackPanel _empty = new() { Spacing = 8, Margin = new Thickness(0, 40, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TagPageArgs _args;

    /// <summary>Shown by setting the frame's content, since pages written in code aren't known to XAML navigation.</summary>
    public TagReferencesPage(TagPageArgs args)
    {
        _args = args;
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(32, 20, 32, 24) };
        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(_title);
        header.Children.Add(_subtitle);
        panel.Children.Add(header);
        panel.Children.Add(_empty);
        panel.Children.Add(_groups);
        Content = new ScrollViewer { Content = panel };
        _groups.Services = _args.Services;
        _groups.Changed = () => { Load(); _args.Services.NotifyLibraryChanged(); return Task.CompletedTask; };
        Load();
    }

    private void Load()
    {
        var tag = _args.Services.WithStore(s => s.Tag(_args.TagId));
        _empty.Children.Clear();
        if (tag is null)
        {
            _title.Text = "Tag Not Found";
            _subtitle.Text = "This tag may have been deleted or merged.";
            _groups.Show([]);
            return;
        }
        var entries = _args.Services.WithStore(s => s.ReferencesWithTag(tag.Id));
        _title.Text = tag.Name;
        _subtitle.Text = $"{entries.Count:N0} {(entries.Count == 1 ? "reference" : "references")}";
        if (entries.Count == 0)
            _empty.Children.Add(new TextBlock
            {
                Text = "Nothing has this tag yet. Choose Edit Tags on a reference to add it.",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        _groups.Show(entries.Select(e => new ResultRow(e, [])).ToList());
    }
}

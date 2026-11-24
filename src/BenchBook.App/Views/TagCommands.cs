using BenchBook.App.Services;
using BenchBook.Core.Model;
using BenchBook.Core.Store;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>
/// Tag commands shared by the sidebar, the tag manager and the gallery. Each
/// returns true when it changed the library.
/// </summary>
internal static class TagCommands
{
    public static async Task<bool> NewTag(XamlRoot root, AppServices services)
    {
        var name = new TextBox { Header = "Name", PlaceholderText = "e.g. Cockpit" };
        var dialog = Dialog(root, "New Tag", Stack(name, Caption("Add a tag, to drag onto references")), "Add");
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = TagNames.Parse(name.Text).Count > 0;
        dialog.IsPrimaryButtonEnabled = false;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        return await Run(root, "Couldn't Save Tag", () => services.WithStore(s => s.CreateTag(name.Text)));
    }

    public static async Task<bool> Rename(XamlRoot root, AppServices services, Tag tag)
    {
        var name = new TextBox { Header = "Name", Text = tag.Name, PlaceholderText = "e.g. Cockpit" };
        var dialog = Dialog(root, "Rename Tag", name, "Rename");
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = TagNames.Parse(name.Text).Count > 0;
        dialog.Opened += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

        var newName = TagNames.Parse(name.Text).FirstOrDefault();
        if (newName is null) return false;
        var other = services.WithStore(s => s.TagNamed(newName));
        if (other is not null && !Ids.Comparer.Equals(other.Id, tag.Id))
        {
            var merge = Dialog(root, $"“{other.Name}” Already Exists",
                Wrapped($"Merge the two tags? References and build photos tagged with either will have “{newName}”."), "Merge Tags");
            if (await merge.ShowAsync() != ContentDialogResult.Primary) return false;
        }
        return await Run(root, "Couldn't Save Tag", () => services.WithStore(s => s.RenameTag(tag.Id, newName)));
    }

    public static async Task<bool> Delete(XamlRoot root, AppServices services, IReadOnlyList<TagUsage> tags)
    {
        if (tags.Count == 0) return false;
        var references = tags.Sum(t => t.References);
        var photos = tags.Sum(t => t.BuildPhotos);
        static string Count(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";
        // As the Mac words it (TagManagerView.swift).
        var message = (references, photos) switch
        {
            (0, 0) => "No references or build photos use them.",
            (_, 0) => $"They'll be removed from {Count(references, "reference", "references")}, which aren't deleted.",
            (0, _) => $"They'll be removed from {Count(photos, "build photo", "build photos")}, which aren't deleted.",
            _ => $"They'll be removed from {Count(references, "reference", "references")} and {Count(photos, "build photo", "build photos")}, which aren't deleted.",
        };
        var dialog = Dialog(root,
            tags.Count == 1 ? $"Delete “{tags[0].Tag.Name}”?" : $"Delete {tags.Count:N0} Tags?",
            Wrapped(message),
            "Delete");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        return await Run(root, "Couldn't Change Tags", () => services.WithStore(s => { s.DeleteTags(tags.Select(t => t.Tag.Id).ToList()); return 0; }));
    }

    public static async Task<bool> Merge(XamlRoot root, AppServices services, IReadOnlyList<TagUsage> tags)
    {
        if (tags.Count < 2) return false;
        var choices = new RadioButtons();
        foreach (var tag in tags) choices.Items.Add($"Keep “{tag.Tag.Name}”");
        choices.SelectedIndex = 0;
        var dialog = Dialog(root, $"Merge {tags.Count:N0} Tags?",
            Stack(Wrapped("References and build photos with any of these tags will have the one you keep. The others are deleted."), choices), "Merge Tags");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        var target = tags[Math.Max(0, choices.SelectedIndex)].Tag.Id;
        return await Run(root, "Couldn't Change Tags", () => services.WithStore(s => { s.MergeTags(tags.Select(t => t.Tag.Id).ToList(), target); return 0; }));
    }

    /// <summary>"Colour ▸": No Colour, then the twelve colours.</summary>
    public static MenuFlyoutSubItem ColourMenu(AppServices services, IReadOnlyList<Tag> tags, Func<Task> changed)
    {
        var menu = new MenuFlyoutSubItem { Text = "Colour" };
        var current = tags.Select(t => t.Color).Distinct().Count() == 1 ? tags[0].Color : "mixed";
        void Add(string text, string? color, IconElement icon)
        {
            var item = new RadioMenuFlyoutItem { Text = text, GroupName = "tagColour", IsChecked = current == color, Icon = icon };
            item.Click += async (_, _) =>
            {
                services.WithStore(s => { s.SetTagColor(tags.Select(t => t.Id).ToList(), color); return 0; });
                await changed();
            };
            menu.Items.Add(item);
        }
        Add("No Colour", null, new FontIcon { Glyph = "\uE91F" });
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var color in TagNames.Colors)
            Add(NamedColors.DisplayName(color), color, new FontIcon { Glyph = "\uEA3B", Foreground = new SolidColorBrush(NamedColors.TagColor(color)) });
        return menu;
    }

    /// <summary>A small coloured dot for a tag.</summary>
    public static FontIcon Dot(string? color, double size = 10) =>
        new() { Glyph = "\uEA3B", FontSize = size, Foreground = new SolidColorBrush(NamedColors.TagColor(color)) };

    private static async Task<bool> Run<T>(XamlRoot root, string title, Func<T> work)
    {
        try
        {
            work();
            return true;
        }
        catch (Exception error) when (error is TagException or SqliteException)
        {
            await Dialog(root, title, Wrapped(error.Message), null).ShowAsync();
            return false;
        }
    }

    private static ContentDialog Dialog(XamlRoot root, string title, object content, string? primary) => new()
    {
        XamlRoot = root,
        Title = title,
        Content = content,
        PrimaryButtonText = primary ?? "",
        CloseButtonText = primary is null ? "OK" : "Cancel",
        DefaultButton = primary is null ? ContentDialogButton.Close : ContentDialogButton.Primary,
    };

    private static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    private static TextBlock Wrapped(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };
}

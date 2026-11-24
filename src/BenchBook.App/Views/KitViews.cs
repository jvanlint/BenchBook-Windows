using BenchBook.App.Services;
using BenchBook.Core.Kits;
using BenchBook.Core.Model;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BenchBook.App.Views;

/// <summary>Pieces the Kits screen, a project's Kits tab, the kit picker and the shopping list share.</summary>
internal static class KitViews
{
    /// <summary>Box art, or a box icon while there's none.</summary>
    public static Grid Picture(AppServices services, string? assetId, double size = 40)
    {
        var holder = new Grid { Width = size, Height = size, CornerRadius = new CornerRadius(4), Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"] };
        holder.Children.Add(new FontIcon { Glyph = "", FontSize = size * 0.4, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] });
        if (assetId is not null) _ = LoadPicture(services, holder, assetId, size);
        return holder;
    }

    private static async Task LoadPicture(AppServices services, Grid holder, string assetId, double size)
    {
        if (services.WithStore(s => s.Asset(assetId)) is not { } asset || await services.Thumbnails.PathFor(asset) is not { } path) return;
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = (int)(size * 2) };
            using (var stream = File.OpenRead(path)) await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            holder.Children.Clear();
            holder.Children.Add(new Image { Source = bitmap, Stretch = Stretch.UniformToFill });
        }
        catch (Exception)
        {
            // The icon stays.
        }
    }

    /// <summary>"★★★☆☆", or nothing for no rating.</summary>
    public static string Stars(Kit kit) => KitText.Stars(kit) is { } stars ? new string('★', stars) + new string('☆', 5 - stars) : "";

    /// <summary>A small button that opens the kit's link, with the site as its tooltip.</summary>
    public static Button? LinkButton(Kit kit)
    {
        if (kit.Link is not { } link || KitText.LinkHost(kit) is not { } host) return null;
        var button = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(6) };
        ToolTipService.SetToolTip(button, $"Open {host}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Open Link");
        button.Click += async (_, _) => await Opener.OpenLink(link);
        return button;
    }

    /// <summary>Status ▸ with BNIB, Opened, WIP and Built, for owned kits.</summary>
    public static MenuFlyoutSubItem StatusMenu(AppServices services, IReadOnlyList<Kit> kits, Action changed)
    {
        var menu = new MenuFlyoutSubItem { Text = "Status" };
        var owned = kits.Where(k => k.Owned).ToList();
        menu.IsEnabled = owned.Count > 0;
        foreach (var status in KitStatus.All)
        {
            var option = new RadioMenuFlyoutItem
            {
                Text = KitText.StatusName(status), GroupName = "kitStatus",
                IsChecked = owned.Count > 0 && owned.All(k => KitText.Shown(k.Status) == status),
            };
            ToolTipService.SetToolTip(option, KitText.LongName(status));
            option.Click += (_, _) =>
            {
                services.WithStore(s => { KitService.SetStatus(s, owned, status); return 0; });
                changed();
            };
            menu.Items.Add(option);
        }
        return menu;
    }

    /// <summary>A status menu in a row for an owned kit, or "Not Owned".</summary>
    public static FrameworkElement StatusCell(AppServices services, Kit kit, Action changed)
    {
        if (!kit.Owned) return TableRows.Text("Not Owned", secondary: true);
        var box = new ComboBox { MinWidth = 110 };
        foreach (var status in KitStatus.All)
        {
            var item = new ComboBoxItem { Content = KitText.StatusName(status), Tag = status };
            ToolTipService.SetToolTip(item, KitText.LongName(status));
            box.Items.Add(item);
        }
        box.SelectedIndex = KitStatus.All.ToList().IndexOf(KitText.Shown(kit.Status));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"Status of {KitText.DisplayName(kit)}");
        box.SelectionChanged += (_, _) =>
        {
            if ((box.SelectedItem as ComboBoxItem)?.Tag is not string status || status == KitText.Shown(kit.Status)) return;
            services.WithStore(s => { KitService.SetStatus(s, new[] { kit }, status); return 0; });
            changed();
        };
        return box;
    }

    /// <summary>Opens the kit editor. True if the kit was saved.</summary>
    public static async Task<Kit?> Edit(XamlRoot root, AppServices services, Kit? kit, Project? project = null)
    {
        var dialog = new KitEditorDialog(services, kit, project) { XamlRoot = root };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? dialog.Saved : null;
    }

    /// <summary>Asks, then deletes the kits and pictures nothing else uses. True if they were deleted.</summary>
    public static async Task<bool> Delete(XamlRoot root, AppServices services, IReadOnlyList<Kit> kits)
    {
        if (kits.Count == 0) return false;
        var projects = services.WithStore(s => s.Projects()).ToDictionary(p => p.Id, Ids.Comparer);
        var (title, message) = KitService.DeletionMessage(kits, projects);
        var confirm = new ContentDialog
        {
            XamlRoot = root, Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return false;
        try
        {
            services.WithStore(s => { KitService.Delete(s, services.Location, kits.ToList()); return 0; });
        }
        catch (SqliteException error)
        {
            await ReferenceActions.Message(root, "Couldn't Delete", error.Message);
        }
        return true;
    }
}

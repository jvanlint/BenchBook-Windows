using BenchBook.App.Services;
using BenchBook.Core.Capture;
using BenchBook.Core.Kits;
using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>"New Kit" or "Edit Kit" (screens.md, "Kit editor").</summary>
internal sealed class KitEditorDialog : ContentDialog
{
    private readonly AppServices _services;
    private readonly Kit? _kit;
    private readonly Project? _project;
    private readonly List<Kit> _others;
    private readonly TextBox _brand = new() { Header = "Brand", PlaceholderText = "e.g. Italeri" };
    private readonly TextBox _name = new() { Header = "Name", PlaceholderText = "e.g. Super Cobra" };
    private readonly TextBox _scale = new() { Header = "Scale", PlaceholderText = "e.g. 1:72" };
    private readonly TextBox _number = new() { Header = "Number", PlaceholderText = "Optional" };
    private readonly TextBlock _copies = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly CheckBox _owned = new() { Content = "Owned" };
    private readonly ComboBox _status = new() { Header = "Status", MinWidth = 200 };
    private readonly TextBlock _statusFooter = new() { Text = "Kits you don't own yet have no status.", TextWrapping = TextWrapping.Wrap };
    private readonly RatingControl _rating = new() { MaxRating = 5, IsClearEnabled = true, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _link = new() { Header = "Link", PlaceholderText = "Scalemates, a shop or the maker" };
    private readonly TextBlock _linkError = new() { Text = "Enter a web address, such as scalemates.com/kits/…", Visibility = Visibility.Collapsed };
    private readonly TextBox _pricePaid = new() { Header = "Price Paid" };
    private readonly CheckBox _forSale = new() { Content = "For Sale" };
    private readonly TextBox _salePrice = new() { Header = "Sale Price" };
    private readonly TextBlock _priceError = new() { Text = "Enter prices as numbers, such as 18.50.", Visibility = Visibility.Collapsed };
    private readonly TextBox _notes = new() { Header = "Notes", PlaceholderText = "Optional", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
    private readonly PictureWell _picture;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    /// <param name="project">For a new kit: the project it's added to, if any.</param>
    public KitEditorDialog(AppServices services, Kit? kit, Project? project = null)
    {
        _services = services;
        _kit = kit;
        _project = project;
        _others = services.WithStore(s => s.Kits());
        Title = kit is null ? "New Kit" : "Edit Kit";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        var critical = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        foreach (var block in new[] { _linkError, _priceError }) { block.Foreground = critical; block.Style = caption; }
        _error.Foreground = critical;
        foreach (var block in new[] { _copies, _statusFooter }) { block.Foreground = secondary; block.Style = caption; }

        foreach (var status in KitStatus.All)
        {
            var item = new ComboBoxItem { Content = KitText.StatusName(status), Tag = status };
            ToolTipService.SetToolTip(item, KitText.LongName(status));
            _status.Items.Add(item);
        }
        var example = KitPrice.Display(18.50m);
        _pricePaid.PlaceholderText = $"e.g. {example}";
        _salePrice.PlaceholderText = "Optional";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_rating, "Rating");
        ToolTipService.SetToolTip(_rating, "Click a star to rate the kit, or the same star again to clear it");

        var draft = kit is null ? new KitDraft() : KitDraft.From(kit);
        _brand.Text = draft.Brand;
        _name.Text = draft.Name;
        _scale.Text = draft.Scale;
        _number.Text = draft.Number;
        _owned.IsChecked = draft.Owned;
        _status.SelectedIndex = KitStatus.All.ToList().IndexOf(draft.Status);
        _rating.Value = draft.Stars ?? -1;
        _link.Text = draft.Link;
        _pricePaid.Text = draft.PricePaid;
        _forSale.IsChecked = draft.ForSale;
        _salePrice.Text = draft.SalePrice;
        _notes.Text = draft.Notes;

        _picture = new PictureWell(services, () => Draft.ParsedLink is { } link ? new Uri(link) : null, "Box art", kit?.PictureAssetId);
        _picture.Problem += ShowError;
        // Dropping a link on the picture fills in Link if it's empty.
        _picture.LinkDropped += url => { if (_link.Text.Trim().Length == 0) _link.Text = url.AbsoluteUri; };

        foreach (var box in new[] { _brand, _name, _scale, _number, _link, _pricePaid, _salePrice }) box.TextChanged += (_, _) => UpdateState();
        _owned.Click += (_, _) => UpdateState();
        _forSale.Click += (_, _) => UpdateState();

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(_scale);
        Grid.SetColumn(_number, 1);
        row.Children.Add(_number);

        var ratingPanel = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Rating" }, _rating } };
        var panel = new StackPanel { Spacing = 12, MinWidth = 460 };
        foreach (var child in new UIElement[]
                 {
                     _brand, _name, row, _copies,
                     _owned, _status, _statusFooter, ratingPanel,
                     _link, _linkError,
                     _pricePaid, _forSale, _salePrice, _priceError,
                     _picture, _notes, _error,
                 })
            panel.Children.Add(child);
        Content = new ScrollViewer { Content = panel, MaxHeight = 620 };

        PrimaryButtonClick += Save_Click;
        Opened += (_, _) => _brand.Focus(FocusState.Programmatic);
        UpdateState();
    }

    /// <summary>The kit saved, once the dialog closes with Save.</summary>
    public Kit? Saved { get; private set; }

    private KitDraft Draft => new()
    {
        Brand = _brand.Text, Name = _name.Text, Scale = _scale.Text, Number = _number.Text,
        Owned = _owned.IsChecked == true,
        Status = (_status.SelectedItem as ComboBoxItem)?.Tag as string ?? KitStatus.Bnib,
        Stars = _rating.Value is >= 1 and <= 5 ? (int)Math.Round(_rating.Value) : null,
        Link = _link.Text, PricePaid = _pricePaid.Text, ForSale = _forSale.IsChecked == true, SalePrice = _salePrice.Text,
        Notes = _notes.Text.Replace("\r\n", "\n").Replace('\r', '\n'),
    };

    private void UpdateState()
    {
        var draft = Draft;
        IsPrimaryButtonEnabled = draft.IsValid;
        _linkError.Visibility = draft.HasValidLink ? Visibility.Collapsed : Visibility.Visible;
        _priceError.Visibility = draft.HasValidPricePaid && draft.HasValidSalePrice ? Visibility.Collapsed : Visibility.Visible;
        _status.IsEnabled = draft.Owned;
        _statusFooter.Visibility = draft.Owned ? Visibility.Collapsed : Visibility.Visible;
        _salePrice.IsEnabled = draft.ForSale;
        _picture?.UpdatePage();

        // Same kit: a note, never a reason not to save.
        var copies = draft.HasValidBrand ? KitService.Copies(_others, draft, _kit).Count : 0;
        var message = KitService.CopiesMessage(copies, editing: _kit is not null);
        _copies.Text = message ?? "";
        _copies.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }

    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var draft = Draft;
        try
        {
            var kit = _services.WithStore(s => _kit is null ? KitService.Create(s, draft, _project) : KitService.Update(s, _kit, draft));
            if (_picture.PendingImage is { } path)
                KitService.SetPicture(_services, _services.Location, kit, path, _picture.PendingName ?? Path.GetFileName(path), Thumbnails.PdfHasPages);
            else if (_picture.RemovePicture)
                _services.WithStore(s => { KitService.RemovePicture(s, _services.Location, kit); return 0; });
            Saved = kit;
        }
        catch (Exception error) when (error is KitException or AftermarketException or CaptureException or SqliteException or IOException or UnauthorizedAccessException)
        {
            args.Cancel = true;
            ShowError($"Couldn't Save Kit. {error.Message}");
        }
    }
}

using BenchBook.App.Services;
using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>"New Part" or "Edit Part" (screens.md, "Aftermarket editor").</summary>
internal sealed class AftermarketEditorDialog : ContentDialog
{
    private readonly AppServices _services;
    private readonly Project _project;
    private readonly AftermarketItem? _item;
    private readonly TextBox _name = new() { Header = "Name", PlaceholderText = "e.g. 50th Anniversary 2015" };
    private readonly TextBox _manufacturer = new() { Header = "Manufacturer", PlaceholderText = "e.g. Galaxy Models" };
    private readonly TextBox _number = new() { Header = "Product Number", PlaceholderText = "Optional" };
    private readonly ComboBox _type = new() { Header = "Type", MinWidth = 200 };
    private readonly ComboBox _status = new() { Header = "Status", MinWidth = 200 };
    private readonly TextBox _scale = new() { Header = "Scale or Fits", PlaceholderText = "e.g. 1:48, Tamiya F-16C" };
    private readonly TextBox _url = new() { Header = "Product Page", PlaceholderText = "https://…" };
    private readonly TextBlock _urlError = new() { Text = "Enter a web address, such as hannants.co.uk/product/…", Visibility = Visibility.Collapsed };
    private readonly TextBox _notes = new() { Header = "Notes", PlaceholderText = "Optional", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
    private readonly PictureWell _picture;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    public AftermarketEditorDialog(AppServices services, Project project, AftermarketItem? item)
    {
        _services = services;
        _project = project;
        _item = item;
        Title = item is null ? "New Part" : "Edit Part";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        _urlError.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        _urlError.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _error.Foreground = _urlError.Foreground;

        foreach (var type in AftermarketKinds.Types) _type.Items.Add(AftermarketKinds.TypeName(type));
        foreach (var status in AftermarketKinds.Statuses) _status.Items.Add(AftermarketKinds.StatusName(status));

        var draft = item is null ? AftermarketDraft.For(project) : AftermarketDraft.From(item);
        _name.Text = draft.ProductName;
        _manufacturer.Text = draft.Manufacturer;
        _number.Text = draft.ProductNumber;
        _scale.Text = draft.ScaleCompatibility;
        _url.Text = draft.ProductUrl;
        _notes.Text = draft.Notes;
        // A type or status this version doesn't know is kept unless it's changed.
        _unknownType = AftermarketKinds.Types.Contains(draft.Type) ? null : draft.Type;
        _unknownStatus = AftermarketKinds.Statuses.Contains(draft.Status) ? null : draft.Status;
        _type.SelectedIndex = Math.Max(0, AftermarketKinds.Types.ToList().IndexOf(draft.Type is var t && _unknownType is null ? t : "other"));
        _status.SelectedIndex = Math.Max(0, AftermarketKinds.Statuses.ToList().IndexOf(draft.Status));
        _type.SelectionChanged += (_, _) => _unknownType = null;
        _status.SelectionChanged += (_, _) => _unknownStatus = null;

        _picture = new PictureWell(services, () => Draft.ParsedUrl, "Picture of the part", item?.ImageAssetId);
        _picture.Problem += ShowError;
        _url.TextChanged += (_, _) => UpdateState();
        _name.TextChanged += (_, _) => UpdateState();

        var panel = new StackPanel { Spacing = 12, MinWidth = 460 };
        foreach (var child in new UIElement[] { _name, _manufacturer, _number, _type, _status, _scale, _url, _urlError, _notes, _picture, _error })
            panel.Children.Add(child);
        Content = new ScrollViewer { Content = panel, MaxHeight = 600 };

        PrimaryButtonClick += Save_Click;
        Opened += (_, _) => _name.Focus(FocusState.Programmatic);
        UpdateState();
    }

    private string? _unknownType;
    private string? _unknownStatus;

    private AftermarketDraft Draft => new()
    {
        ProductName = _name.Text, Manufacturer = _manufacturer.Text, ProductNumber = _number.Text,
        Type = _unknownType ?? AftermarketKinds.Types[Math.Max(0, _type.SelectedIndex)],
        Status = _unknownStatus ?? AftermarketKinds.Statuses[Math.Max(0, _status.SelectedIndex)],
        ScaleCompatibility = _scale.Text, ProductUrl = _url.Text, Notes = _notes.Text.Replace("\r\n", "\n").Replace('\r', '\n'),
    };

    private void UpdateState()
    {
        var draft = Draft;
        IsPrimaryButtonEnabled = draft.HasValidName && draft.HasValidUrl;
        _urlError.Visibility = draft.HasValidUrl ? Visibility.Collapsed : Visibility.Visible;
        _picture?.UpdatePage();
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
            var item = _services.WithStore(s => _item is null
                ? AftermarketService.Create(s, _project.Id, draft)
                : AftermarketService.Update(s, _item, draft));
            if (_picture.PendingImage is { } path)
                AftermarketService.SetImage(_services, _services.Location, item, path, _picture.PendingName ?? Path.GetFileName(path), Thumbnails.PdfHasPages);
            else if (_picture.RemovePicture)
                _services.WithStore(s => { AftermarketService.RemoveImage(s, _services.Location, item); return 0; });
        }
        catch (Exception error) when (error is AftermarketException or CaptureException or SqliteException or IOException or UnauthorizedAccessException)
        {
            args.Cancel = true;
            ShowError($"Couldn't Save Part. {error.Message}");
        }
    }
}

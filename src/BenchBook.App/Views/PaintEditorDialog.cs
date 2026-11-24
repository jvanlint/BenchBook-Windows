using BenchBook.App.Services;
using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace BenchBook.App.Views;

/// <summary>Paint swatches: a filled circle, or a dashed outline when there's no colour.</summary>
internal static class PaintSwatch
{
    public static FrameworkElement Make(string? hex, double size = 16)
    {
        var circle = new Ellipse { Width = size, Height = size, StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center };
        if (Swatch.Components(hex) is var (r, g, b))
        {
            circle.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
            circle.Stroke = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        }
        else
        {
            circle.Stroke = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
            circle.StrokeDashArray = [2, 2];
        }
        return circle;
    }

    public static string Hex(Windows.UI.Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}

/// <summary>"New Paint" or "Edit Paint" (screens.md, "Paint editor").</summary>
internal sealed class PaintEditorDialog : ContentDialog
{
    private readonly AppServices _services;
    private readonly Paint? _paint;
    private readonly bool _wasOwned;
    private readonly TextBox _brand = new() { Header = "Brand", PlaceholderText = "Required, e.g. Tamiya" };
    private readonly TextBox _range = new() { Header = "Range", PlaceholderText = "e.g. Acrylic XF" };
    private readonly TextBox _code = new() { Header = "Code", PlaceholderText = "Required, e.g. XF-54" };
    private readonly TextBox _name = new() { Header = "Name", PlaceholderText = "e.g. Dark Sea Grey" };
    private readonly ComboBox _type = new() { Header = "Type", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _system = new() { Header = "Standard", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _standardCode = new() { Header = "Standard Code" };
    private readonly ComboBox _match = new() { Header = "Match", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _standardFooter = new()
    {
        Text = "Most paints don't follow a standard. Choose one when you use this paint for a standard colour, such as FS 36270.",
        TextWrapping = TextWrapping.Wrap,
    };

    // Values this version doesn't know are kept unless the user changes that menu.
    private string _systemRaw = "";
    private string _matchRaw = "";
    private string _typeRaw = "";
    private readonly CheckBox _showSwatch = new() { Content = "Show a Swatch" };
    private readonly ColorPicker _colour = new() { IsAlphaEnabled = false, IsMoreButtonVisible = true, ColorSpectrumShape = ColorSpectrumShape.Ring };
    private readonly CheckBox _owned = new() { Content = "I Own This Paint" };
    private readonly TextBox _notes = new() { Header = "Notes", PlaceholderText = "Optional, e.g. thin 1:1 with X-20A", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };

    public PaintEditorDialog(AppServices services, Paint? paint, bool owned, string? standard = null)
    {
        _services = services;
        _paint = paint;
        _wasOwned = owned;
        Title = paint is null ? "New Paint" : "Edit Paint";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        _hint.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _hint.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _standardFooter.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _standardFooter.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        if (paint is not null)
        {
            _brand.Text = paint.Brand;
            _range.Text = paint.Range;
            _code.Text = paint.Code;
            _name.Text = paint.Name;
            _systemRaw = paint.StandardSystem;
            _standardCode.Text = paint.StandardCode;
            _matchRaw = paint.Match;
            _typeRaw = paint.Type;
            _notes.Text = paint.Notes ?? "";
        }
        else if (PaintStandardParser.Parse(standard) is { } guide)
        {
            // A new paint for a guide colour starts with its standard.
            _systemRaw = guide.SystemRaw;
            _standardCode.Text = guide.Code;
        }
        FillMenus();
        _owned.IsChecked = owned;
        _showSwatch.IsChecked = paint?.SwatchHex is not null;
        if (Swatch.Components(paint?.SwatchHex) is var (r, g, b)) _colour.Color = Windows.UI.Color.FromArgb(255, r, g, b);
        else _colour.Color = Windows.UI.Color.FromArgb(255, 0x80, 0x80, 0x80);

        var swatchNote = new TextBlock
        {
            Text = "The swatch only helps you recognise the paint on screen; it isn't an accurate colour match.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var swatchPanel = new StackPanel { Spacing = 6, Children = { _colour, swatchNote } };
        void ShowSwatch() => swatchPanel.Visibility = _showSwatch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _showSwatch.Checked += (_, _) => ShowSwatch();
        _showSwatch.Unchecked += (_, _) => ShowSwatch();
        ShowSwatch();

        _brand.TextChanged += (_, _) => UpdateCanSave();
        _code.TextChanged += (_, _) => UpdateCanSave();
        UpdateCanSave();

        var standardHeading = new TextBlock { Text = "Colour Standard", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        foreach (var child in new UIElement[] { _brand, _range, _code, _name, _type, standardHeading, _system, _standardCode, _match, _standardFooter, _showSwatch, swatchPanel, _owned, _notes, _hint })
            panel.Children.Add(child);
        Content = new ScrollViewer { Content = panel, MaxHeight = 560 };
        PrimaryButtonClick += Save_Click;
        Opened += (_, _) => _brand.Focus(FocusState.Programmatic);
    }

    /// <summary>The paint saved, once the dialog closes with Save.</summary>
    public Paint? Saved { get; private set; }

    /// <summary>Type, Standard and Match menus, each with its hint (screens.md, "Paint editor").</summary>
    private void FillMenus()
    {
        static ComboBoxItem Item(string text, string tag, string? hint = null)
        {
            var item = new ComboBoxItem { Content = text, Tag = tag };
            if (hint is not null) ToolTipService.SetToolTip(item, hint);
            return item;
        }

        _type.Items.Add(Item("Not Set", ""));
        foreach (var type in PaintType.All) _type.Items.Add(Item(type.Shown, type.Raw, type.Hint));
        _type.SelectedIndex = PaintType.FromRaw(_typeRaw) is { } known ? PaintType.All.ToList().IndexOf(known) + 1 : 0;
        void TypeHelp() => ToolTipService.SetToolTip(_type, PaintType.FromRaw(Selected(_type)) is { } t ? t.Hint : "What the paint is made with, so you use the right thinner");
        TypeHelp();
        _type.SelectionChanged += (_, _) => { _typeRaw = Selected(_type); TypeHelp(); };

        _system.Items.Add(Item("None", ""));
        foreach (var system in PaintStandardSystem.All) _system.Items.Add(Item(system.MenuName, system.Raw, system.Subjects));
        // An unknown system shows as Custom, with its code, and is kept.
        var shown = _systemRaw.Length == 0 ? null : PaintStandardSystem.FromRaw(_systemRaw) ?? PaintStandardSystem.Custom;
        _system.SelectedIndex = shown is null ? 0 : PaintStandardSystem.All.ToList().IndexOf(shown) + 1;
        _system.SelectionChanged += (_, _) => { _systemRaw = Selected(_system); UpdateStandard(); };

        _match.Items.Add(Item("Not Set", ""));
        foreach (var match in PaintMatch.All) _match.Items.Add(Item(match.Shown, match.Raw));
        _match.SelectedIndex = PaintMatch.FromRaw(_matchRaw) is { } m ? PaintMatch.All.ToList().IndexOf(m) + 1 : 0;
        ToolTipService.SetToolTip(_match, "How closely this paint matches the standard");
        _match.SelectionChanged += (_, _) => _matchRaw = Selected(_match);
        UpdateStandard();
    }

    private static string Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    /// <summary>With a system chosen, its code and match; with None, a footer.</summary>
    private void UpdateStandard()
    {
        var has = _systemRaw.Length > 0;
        _standardCode.Visibility = _match.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _standardFooter.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        _standardCode.PlaceholderText = (PaintStandardSystem.FromRaw(_systemRaw) ?? PaintStandardSystem.Custom).Prompt;
    }

    private PaintDraft Draft => new()
    {
        Brand = _brand.Text, Range = _range.Text, Code = _code.Text, Name = _name.Text,
        StandardSystem = _systemRaw, StandardCode = _standardCode.Text, Match = _matchRaw, Type = _typeRaw,
        IsMix = _paint?.IsMix ?? false,
        Notes = _notes.Text.Replace("\r\n", "\n").Replace('\r', '\n'),
        SwatchHex = _showSwatch.IsChecked == true ? PaintSwatch.Hex(_colour.Color) : null,
        IsOwned = _owned.IsChecked == true,
    };

    private void UpdateCanSave()
    {
        var valid = !Draft.MissingRequiredField;
        IsPrimaryButtonEnabled = valid;
        _hint.Text = valid ? "" : "Enter a brand and a code to save the paint.";
    }

    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var draft = Draft;
        try
        {
            Saved = _services.WithStore(s => _paint is null ? PaintService.Create(s, draft) : PaintService.Update(s, _paint, _wasOwned, draft));
        }
        catch (Exception error) when (error is PaintException or SqliteException)
        {
            args.Cancel = true;
            _hint.Text = $"Couldn't Save Paint. {error.Message}";
        }
    }
}

using BenchBook.App.Services;
using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BenchBook.App.Views;

/// <summary>"New Paint", "Edit Paint", "New Mix" or "Edit Mix" (screens.md, "Paint editor").</summary>
internal sealed class PaintEditorDialog : ContentDialog
{
    private readonly AppServices _services;
    private readonly Paint? _paint;
    private readonly bool _wasOwned;
    private readonly bool _isMix;
    private readonly TextBox _brand = new() { Header = "Brand" };
    private readonly TextBox _range = new() { Header = "Range", PlaceholderText = "e.g. Acrylic XF" };
    private readonly TextBox _code = new() { Header = "Code" };
    private readonly TextBox _name = new() { Header = "Name" };
    private readonly ComboBox _type = new() { Header = "Type", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _system = new() { Header = "Standard", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _standardCode = new() { Header = "Standard Code" };
    private readonly ComboBox _match = new() { Header = "Match", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _standardFooter = Caption("Most paints don't follow a standard. Choose one when you use this paint for a standard colour, such as FS 36270.");

    // Values this version doesn't know are kept unless the user changes that menu.
    private string _systemRaw = "";
    private string _matchRaw = "";
    private string _typeRaw = "";
    private readonly CheckBox _showSwatch = new() { Content = "Show a Swatch" };
    private readonly ColorPicker _colour = new() { IsAlphaEnabled = false, IsMoreButtonVisible = true, ColorSpectrumShape = ColorSpectrumShape.Ring };
    private readonly ContentControl _preview = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _owned = new() { Content = "I Own This Paint" };
    private readonly TextBox _notes = new() { Header = "Notes", PlaceholderText = "Optional, e.g. thin 1:1 with X-20A", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };

    // A mix's recipe.
    private readonly PaintCatalogue? _catalogue;
    private List<MixPartDraft> _recipe = [];
    private readonly StackPanel _recipeRows = new() { Spacing = 4 };
    private readonly TextBlock _recipeFooter = Caption("");

    /// <param name="owned">Whether the user owns the paint. Ignored for a mix.</param>
    /// <param name="standard">A new paint for a guide colour starts with its standard.</param>
    /// <param name="newMix">A new paint is a mix.</param>
    public PaintEditorDialog(AppServices services, Paint? paint, bool owned, PaintStandard? standard = null, bool newMix = false)
    {
        _services = services;
        _paint = paint;
        _wasOwned = owned;
        _isMix = paint?.IsMix ?? newMix;
        Title = (paint is null, _isMix) switch
        {
            (true, false) => "New Paint",
            (false, false) => "Edit Paint",
            (true, true) => "New Mix",
            (false, true) => "Edit Mix",
        };
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        _hint.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _hint.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _brand.PlaceholderText = _isMix ? "Required, e.g. Custom or your name" : "Required, e.g. Tamiya";
        _code.PlaceholderText = _isMix ? "Optional" : "Required, e.g. XF-54";
        _name.PlaceholderText = _isMix ? "Required, e.g. Faded Sky Grey" : "e.g. Dark Sea Grey";

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
        else if (standard is { } guide)
        {
            // A new paint for a guide colour starts with its standard.
            _systemRaw = guide.SystemRaw;
            _standardCode.Text = guide.Code;
        }
        if (_isMix)
        {
            _catalogue = services.WithStore(PaintCatalogue.Load);
            if (paint is not null) _recipe = _catalogue.Recipe(paint).Select(p => new MixPartDraft(p.PaintId, p.Parts)).ToList();
        }
        FillMenus();
        _owned.IsChecked = owned;
        _showSwatch.IsChecked = paint?.SwatchHex is not null;
        if (Swatch.Components(paint?.SwatchHex) is var (r, g, b)) _colour.Color = Windows.UI.Color.FromArgb(255, r, g, b);
        else _colour.Color = Windows.UI.Color.FromArgb(255, 0x80, 0x80, 0x80);

        // The colour picker and a preview of the swatch: glass for clears.
        var previewRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        previewRow.Children.Add(_preview);
        previewRow.Children.Add(Caption("The swatch only helps you recognise the paint on screen; it isn't an accurate colour match."));
        ((TextBlock)previewRow.Children[1]).MaxWidth = 360;
        var swatchPanel = new StackPanel { Spacing = 6, Children = { _colour, previewRow } };
        void ShowSwatch() => swatchPanel.Visibility = _showSwatch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _showSwatch.Checked += (_, _) => ShowSwatch();
        _showSwatch.Unchecked += (_, _) => ShowSwatch();
        ShowSwatch();
        _colour.ColorChanged += (_, _) => UpdatePreview();
        _name.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        _brand.TextChanged += (_, _) => UpdateCanSave();
        _code.TextChanged += (_, _) => UpdateCanSave();
        _name.TextChanged += (_, _) => UpdateCanSave();

        var standardHeading = Heading("Colour Standard");
        var panel = new StackPanel { Spacing = 12, MinWidth = 460 };
        var fields = new List<UIElement> { _brand };
        if (!_isMix) fields.Add(_range);
        fields.AddRange(new UIElement[] { _code, _name, _type });
        if (_isMix) fields.AddRange(new UIElement[] { Heading("Recipe"), _recipeRows, RecipeAddButton(), _recipeFooter });
        fields.AddRange(new UIElement[] { standardHeading, _system, _standardCode, _match, _standardFooter, _showSwatch, swatchPanel });
        if (!_isMix) fields.Add(_owned);
        fields.AddRange(new UIElement[] { _notes, _hint });
        foreach (var child in fields) panel.Children.Add(child);
        Content = new ScrollViewer { Content = panel, MaxHeight = 560 };
        if (_isMix) ShowRecipe();
        UpdateCanSave();
        PrimaryButtonClick += Save_Click;
        Opened += (_, _) => _brand.Focus(FocusState.Programmatic);
    }

    /// <summary>The paint saved, once the dialog closes with Save.</summary>
    public Paint? Saved { get; private set; }

    private static TextBlock Caption(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static TextBlock Heading(string text) =>
        new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) };

    private void UpdatePreview() =>
        _preview.Content = PaintSwatch.Make(PaintSwatch.Hex(_colour.Color), 28, PaintClarity.IsClear(_name.Text));

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

    // MARK: Recipe

    /// <summary>One row per paint: swatch, name, parts, up and down, and remove.</summary>
    private void ShowRecipe()
    {
        _recipeRows.Children.Clear();
        for (var index = 0; index < _recipe.Count; index++)
        {
            var line = _recipe[index];
            if (_catalogue!.Paint(line.PaintId) is not { } paint) continue;
            var name = PaintText.DisplayName(paint);
            var row = new Grid { ColumnSpacing = 6 };
            TableRows.Columns(row, "20", "1*", "76", "40", "Auto", "Auto", "Auto");
            TableRows.Cell(row, PaintSwatch.Make(paint), 0);
            TableRows.Cell(row, TableRows.Text(name), 1);

            var unit = TableRows.Text(MixAmount.Unit(line.Parts), secondary: true);
            var parts = new NumberBox
            {
                Value = line.Parts, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                // Up to two decimals, as recipes show them.
                NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
                {
                    FractionDigits = 0, IsDecimalPointAlwaysDisplayed = false,
                    NumberRounder = new Windows.Globalization.NumberFormatting.IncrementNumberRounder { Increment = 0.01 },
                },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(parts, $"Parts of {name}");
            var position = index;
            parts.ValueChanged += (_, e) =>
            {
                // Empty reads as NaN, which isn't more than zero, so Save stays off.
                _recipe[position] = _recipe[position] with { Parts = e.NewValue };
                unit.Text = MixAmount.Unit(e.NewValue);
                UpdateRecipeFooter();
                UpdateCanSave();
            };
            TableRows.Cell(row, parts, 2);
            TableRows.Cell(row, unit, 3);

            var up = IconButton("", $"Move {name} up the recipe", position > 0, () => Move(line.PaintId, -1));
            var down = IconButton("", $"Move {name} down the recipe", position < _recipe.Count - 1, () => Move(line.PaintId, 1));
            var remove = IconButton("", $"Take {name} out of the recipe", true, () =>
            {
                _recipe.RemoveAll(p => Ids.Comparer.Equals(p.PaintId, line.PaintId));
                ShowRecipe();
            });
            TableRows.Cell(row, up, 4);
            TableRows.Cell(row, down, 5);
            TableRows.Cell(row, remove, 6);
            _recipeRows.Children.Add(row);
        }
        _recipeRows.Visibility = _recipe.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRecipeFooter();
        UpdateCanSave();
    }

    private static Button IconButton(string glyph, string help, bool enabled, Action click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Padding = new Thickness(6), IsEnabled = enabled };
        ToolTipService.SetToolTip(button, help);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, help);
        button.Click += (_, _) => click();
        return button;
    }

    private void Move(string paintId, int offset)
    {
        _recipe = (Draft.MoveRecipePaint(paintId, offset)).Recipe.ToList();
        ShowRecipe();
    }

    private void UpdateRecipeFooter()
    {
        if (_catalogue is null) return;
        var paints = _recipe.Select(p => _catalogue.Paint(p.PaintId)).OfType<Paint>().ToList();
        var missing = paints.Where(p => !_catalogue.OwnedIds.Contains(p.Id)).Select(PaintText.DisplayName).ToList();
        _recipeFooter.Text = Draft.HasBadParts
            ? "Each paint needs a number of parts more than zero."
            : paints.Count == 0
                ? "Add the paints you mix, with how many parts of each. Changes for one project go in that project's notes for the colour."
                : missing.Count == 0
                    ? "You own every paint in this recipe, so the mix counts as owned."
                    : $"Not owned yet: {PaintText.List(missing)}. Projects using this mix put them on the Shopping List.";
    }

    /// <summary>
    /// "Add Paints…": Add Paints to Recipe, as a flyout, since a dialog can't
    /// open over this one. Catalogue paints that aren't mixes or already in the
    /// recipe; each is added with 1 part.
    /// </summary>
    private Button RecipeAddButton()
    {
        var button = new Button { Content = "Add Paints…" };
        ToolTipService.SetToolTip(button, "Choose catalogue paints for this mix");
        var search = new TextBox { PlaceholderText = "Search brand, code, name or standard" };
        var list = TableRows.List((grid, item) =>
        {
            var paint = (Paint)item;
            grid.DataContext = paint;
            TableRows.Columns(grid, "20", "1*", "60");
            TableRows.Cell(grid, PaintSwatch.Make(paint), 0);
            TableRows.Cell(grid, TableRows.TwoLines(PaintText.DisplayName(paint), _catalogue!.SecondaryLine(paint)), 1);
            if (_catalogue.IsOwned(paint)) TableRows.Cell(grid, TableRows.Text("Owned", secondary: true), 2);
        }, ListViewSelectionMode.Multiple);
        list.Height = 300;
        var empty = new StackPanel { Spacing = 4, Margin = new Thickness(0, 12, 0, 12) };
        var add = new Button { Content = "Add Paint", Style = (Style)Application.Current.Resources["AccentButtonStyle"], IsEnabled = false };
        var cancel = new Button { Content = "Cancel" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, add } };
        var panel = new StackPanel { Spacing = 10, Width = 440 };
        panel.Children.Add(new TextBlock { Text = "Add Paints to Recipe", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        panel.Children.Add(search);
        panel.Children.Add(list);
        panel.Children.Add(empty);
        panel.Children.Add(buttons);
        var flyout = new Flyout { Content = panel };
        button.Flyout = flyout;

        void Filter()
        {
            var inRecipe = _recipe.Select(p => p.PaintId).ToHashSet(Ids.Comparer);
            var candidates = PaintService.Sorted(_catalogue!.ById.Values.Where(p => !p.IsMix && !inRecipe.Contains(p.Id))).ToList();
            var visible = candidates.Where(p => PaintIdentity.Matches(search.Text, p)).ToList();
            list.ItemsSource = visible;
            empty.Children.Clear();
            var none = visible.Count == 0;
            empty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            list.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
            if (none)
            {
                var noPaints = !_catalogue.ById.Values.Any(p => !p.IsMix);
                empty.Children.Add(new TextBlock { Text = noPaints ? "No Paints in Catalogue" : "No Matching Paints", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
                empty.Children.Add(Caption(noPaints
                    ? "Add the paints you mix to your catalogue first, from Paints in the sidebar."
                    : "No paint that isn't already in the recipe matches the search."));
            }
        }
        void UpdateAdd()
        {
            var count = list.SelectedItems.Count;
            add.IsEnabled = count > 0;
            add.Content = count > 1 ? $"Add {count:N0} Paints" : "Add Paint";
        }
        list.SelectionChanged += (_, _) => UpdateAdd();
        search.TextChanged += (_, _) => { Filter(); UpdateAdd(); };
        flyout.Opening += (_, _) => { search.Text = ""; Filter(); UpdateAdd(); };
        flyout.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        cancel.Click += (_, _) => flyout.Hide();
        add.Click += (_, _) =>
        {
            _recipe.AddRange(list.SelectedItems.OfType<Paint>().Select(p => new MixPartDraft(p.Id, 1)));
            flyout.Hide();
            ShowRecipe();
        };
        return button;
    }

    // MARK: Saving

    private PaintDraft Draft => new()
    {
        Brand = _brand.Text, Range = _isMix ? "" : _range.Text, Code = _code.Text, Name = _name.Text,
        StandardSystem = _systemRaw, StandardCode = _standardCode.Text, Match = _matchRaw, Type = _typeRaw,
        IsMix = _isMix,
        Notes = _notes.Text.Replace("\r\n", "\n").Replace('\r', '\n'),
        SwatchHex = _showSwatch.IsChecked == true ? PaintSwatch.Hex(_colour.Color) : null,
        IsOwned = !_isMix && _owned.IsChecked == true,
        Recipe = _recipe.ToList(),
    };

    private void UpdateCanSave()
    {
        var draft = Draft;
        IsPrimaryButtonEnabled = !draft.MissingRequiredField && !draft.HasBadParts;
        _hint.Text = !draft.MissingRequiredField ? ""
            : _isMix ? "Enter a brand and a name to save the mix." : "Enter a brand and a code to save the paint.";
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
            _hint.Text = $"Couldn't Save {(_isMix ? "Mix" : "Paint")}. {error.Message}";
        }
    }
}

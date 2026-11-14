using BenchBook.App.Services;
using BenchBook.App.ViewModels;
using BenchBook.Core;
using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Graphics;

namespace BenchBook.App.Share;

/// <summary>
/// "Add to BenchBook", shown when BenchBook is chosen in the Windows share sheet
/// (screens.md, "Share sheet"). It runs in its own process and never opens the
/// library: it reads the projects the app published, and leaves what's shared
/// in the inbox for the app to import.
/// </summary>
internal sealed class ShareWindow : Window
{
    private const string CanSave = "BenchBook can save images, PDFs, Markdown files, web links and text.";

    private readonly ShareOperation _operation;
    private readonly LibraryLocation _location = LibraryLocation.Default();
    private readonly CaptureInbox _inbox;
    private readonly Grid _root = new() { Padding = new Thickness(20), RowSpacing = 16 };
    private readonly ContentControl _content = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly Button _add = new() { Content = "Add", Style = (Style)Application.Current.Resources["AccentButtonStyle"], IsEnabled = false, MinWidth = 80 };
    private readonly ComboBox _project = new() { Header = "Project", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _tags = new() { Header = "Tags", PlaceholderText = "e.g. wheel well, cockpit" };
    private readonly TextBox _note = new() { Header = "Note", PlaceholderText = "Optional", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 96 };

    private List<CapturedItem> _items = [];
    private string? _staging;
    private bool _sent;

    public ShareWindow(ShareOperation operation)
    {
        _operation = operation;
        _inbox = new CaptureInbox(_location.InboxFolder);
        Title = "Add to BenchBook";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon("Assets/BenchBook.ico");
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        Size(460, 520);

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.Children.Add(new TextBlock { Text = "Add to BenchBook", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        Grid.SetRow(_content, 1);
        _root.Children.Add(_content);
        var bar = Bar();
        Grid.SetRow(bar, 2);
        _root.Children.Add(bar);
        Content = _root;

        _content.Content = new ProgressRing { IsActive = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _add.Click += (_, _) => Send();
        Closed += (_, _) =>
        {
            // Closed without adding: nothing is left behind.
            if (!_sent && _staging is not null) CaptureInbox.Discard(_staging);
        };
        _root.Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Sizes the window in effective pixels and centres it on its screen.</summary>
    private void Size(int width, int height)
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
        if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary) is { } display)
        {
            var work = display.WorkArea;
            AppWindow.Move(new PointInt32(work.X + (work.Width - AppWindow.Size.Width) / 2, work.Y + (work.Height - AppWindow.Size.Height) / 2));
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private Grid Bar()
    {
        var bar = new Grid { ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(new TextBlock
        {
            Text = "Items are added when BenchBook is open.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        var cancel = new Button { Content = "Cancel", MinWidth = 80 };
        cancel.Click += (_, _) => Close();
        Grid.SetColumn(cancel, 1);
        bar.Children.Add(cancel);
        Grid.SetColumn(_add, 2);
        bar.Children.Add(_add);
        _add.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Enter, Modifiers = Windows.System.VirtualKeyModifiers.Control });
        cancel.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape });
        return bar;
    }

    private async Task LoadAsync()
    {
        var directory = ShareDirectory.Load(_location.ShareProjectsPath);
        if (directory.Projects.Count == 0)
        {
            Message("No Projects", "Open BenchBook and create a project, then share again.");
            return;
        }

        List<string> skipped;
        try
        {
            _staging = _inbox.MakeStagingFolder();
            (_items, skipped) = await CaptureReader.ReadForShare(_operation.Data, _staging);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Message("Couldn't Send to BenchBook", $"What's being shared couldn't be read ({error.Message}).");
            return;
        }

        if (_items.Count == 0)
        {
            Message("Nothing to Add", Unsupported(skipped));
            return;
        }

        var form = new StackPanel { Spacing = 12 };
        var list = new StackPanel { Spacing = 6 };
        foreach (var item in _items.Take(20)) list.Children.Add(ItemRow(item));
        if (_items.Count > 20) list.Children.Add(Secondary($"and {_items.Count - 20:N0} more"));
        if (skipped.Count > 0) list.Children.Add(Secondary(Unsupported(skipped)));
        form.Children.Add(list);

        foreach (var project in directory.Projects)
            _project.Items.Add(new ComboBoxItem { Content = project.Name, Tag = project.Id });
        _project.SelectedItem = _project.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Ids.Comparer.Equals((string)i.Tag, directory.InitialProjectId));
        form.Children.Add(_project);
        form.Children.Add(_tags);
        form.Children.Add(_note);
        _content.Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };
        _add.IsEnabled = true;
        _tags.Focus(FocusState.Programmatic);
    }

    private static FrameworkElement ItemRow(CapturedItem item)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon { Glyph = ReferenceTile.KindGlyph(Kind(item)), FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var text = new TextBlock { Text = Summary(item), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private static string Kind(CapturedItem item) => item switch
    {
        CapturedItem.File file => Path.GetExtension(file.OriginalFilename ?? file.Path).ToLowerInvariant() switch
        {
            ".pdf" => ReferenceKind.Pdf,
            ".md" or ".markdown" or ".mdown" or ".mkd" => ReferenceKind.Markdown,
            _ => ReferenceKind.Image,
        },
        CapturedItem.Link link => VideoLink.Parse(link.Url) is not null ? ReferenceKind.VideoLink : ReferenceKind.Webpage,
        _ => ReferenceKind.TextSnippet,
    };

    private static string Summary(CapturedItem item) => item switch
    {
        CapturedItem.File file => file.OriginalFilename ?? Path.GetFileName(file.Path),
        CapturedItem.Link link => link.Title ?? link.Url,
        CapturedItem.Text text => ReferenceImporter.Cut(text.Content.Trim(), 80),
        _ => "",
    };

    private static string Unsupported(IReadOnlyList<string> skipped) =>
        skipped.Count == 0 ? CanSave : $"Can't add: {string.Join(", ", skipped)}. {CanSave}";

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private void Message(string title, string message)
    {
        var panel = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 340 };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        _content.Content = panel;
        _add.IsEnabled = false;
    }

    private void Send()
    {
        if (_staging is null || _project.SelectedItem is not ComboBoxItem { Tag: string projectId }) return;
        _add.IsEnabled = false;
        var envelope = new CaptureEnvelope
        {
            Id = Ids.New(),
            CreatedAt = DateTimeOffset.UtcNow,
            ProjectId = projectId,
            Tags = TagNames.Parse(_tags.Text),
            Note = string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim(),
            Items = _items.Select(CaptureEnvelope.ItemFor).ToList(),
        };
        try
        {
            _inbox.Commit(envelope, _staging);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Message("Couldn't Send to BenchBook", error.Message);
            return;
        }
        _sent = true;

        // Shared to last: chosen first next time.
        try
        {
            if (ShareDirectory.Load(_location.ShareProjectsPath) is { Projects.Count: > 0 } directory)
                (directory with { ActiveProjectId = projectId }).Save(_location.ShareProjectsPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Only which project is chosen first next time; the capture itself is sent.
        }

        _operation.ReportCompleted();
        Close();
    }
}

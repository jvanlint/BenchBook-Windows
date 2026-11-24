using BenchBook.App.Services;
using BenchBook.Core.BuildPhotos;
using BenchBook.Core.Model;
using BenchBook.Core.Store;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BenchBook.App.Views;

/// <summary>"Build Photo Details": the photo, Stage, Taken, Notes and Tags (screens.md, "Build photo details").</summary>
internal sealed class BuildPhotoDetailsDialog : ContentDialog
{
    private readonly AppServices _services;
    private readonly BuildPhotoEntry _entry;
    private readonly ComboBox _stage = new() { Header = "Stage", MinWidth = 160 };
    private readonly CalendarDatePicker _date = new() { Header = "Taken" };
    private readonly TimePicker _time = new() { Header = " ", MinWidth = 0 };
    private readonly TextBox _notes = new()
    {
        Header = "Notes", PlaceholderText = "Notes (optional). The first line shows under the photo.",
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96,
    };
    private readonly TextBox _tags = new() { Header = "Tags", PlaceholderText = "Tags, separated with commas" };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    public BuildPhotoDetailsDialog(AppServices services, BuildPhotoEntry entry)
    {
        _services = services;
        _entry = entry;
        Title = "Build Photo Details";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        _error.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];

        var photo = entry.Photo;
        _stage.Items.Add(new ComboBoxItem { Content = "WIP", Tag = BuildPhotoStage.Wip });
        _stage.Items.Add(new ComboBoxItem { Content = "Completed", Tag = BuildPhotoStage.Completed });
        _stage.SelectedIndex = BuildPhotoText.IsCompleted(photo) ? 1 : 0;
        var local = photo.TakenAt.ToLocalTime();
        _date.Date = local.Date;
        _time.Time = new TimeSpan(local.Hour, local.Minute, 0);
        _notes.Text = photo.Notes ?? "";
        _tags.Text = string.Join(", ", entry.Tags.Select(t => t.Name));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_time, "Time Taken");

        var picture = new Image { Height = 180, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        _ = LoadPicture(picture);
        var taken = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _date, _time } };
        var panel = new StackPanel { Spacing = 12, MinWidth = 440 };
        foreach (var child in new UIElement[] { picture, _stage, taken, _notes, _tags, _error }) panel.Children.Add(child);
        Content = new ScrollViewer { Content = panel, MaxHeight = 600 };
        PrimaryButtonClick += Save_Click;
        Opened += (_, _) => _notes.Focus(FocusState.Programmatic);
    }

    /// <summary>True once saved, so the caller can reload.</summary>
    public bool Saved { get; private set; }

    private async Task LoadPicture(Image image)
    {
        if (_entry.Asset is not { } asset || await _services.Thumbnails.PathFor(asset) is not { } path) return;
        try
        {
            var bitmap = new BitmapImage { DecodePixelHeight = 360, DecodePixelType = DecodePixelType.Physical };
            using (var stream = File.OpenRead(path)) await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            image.Source = bitmap;
        }
        catch (Exception)
        {
            // Without a thumbnail the details still work.
        }
    }

    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // The pickers show minutes, so a date that wasn't changed stays exactly as it was.
        var original = _entry.Photo.TakenAt.ToLocalTime();
        var day = _date.Date?.Date ?? original.Date;
        var time = new TimeSpan(_time.Time.Hours, _time.Time.Minutes, 0);
        var takenAt = _entry.Photo.TakenAt;
        if (day != original.Date || time != new TimeSpan(original.Hour, original.Minute, 0))
        {
            var local = day + time;
            takenAt = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }
        var stage = (_stage.SelectedItem as ComboBoxItem)?.Tag as string ?? BuildPhotoStage.Wip;
        // An unknown stage, from a newer version, is kept unless the user changes it.
        if (!BuildPhotoText.IsCompleted(_entry.Photo) && stage == BuildPhotoStage.Wip) stage = _entry.Photo.Stage;
        try
        {
            _services.WithStore(s => { BuildPhotoService.Update(s, _entry.Photo, _notes.Text, takenAt, stage, _tags.Text); return 0; });
            Saved = true;
        }
        catch (Exception error) when (error is BuildPhotoException or TagException or SqliteException)
        {
            args.Cancel = true;
            _error.Text = $"Couldn't Save Build Photo. {error.Message}";
            _error.Visibility = Visibility.Visible;
        }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BenchBook.App.Views;

/// <summary>An empty state, or a screen from a later phase.</summary>
public sealed record Placeholder(string Title, string Subtitle, string? Body = null);

public sealed partial class PlaceholderPage : Page
{
    public PlaceholderPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not Placeholder placeholder) return;
        TitleText.Text = placeholder.Title;
        SubtitleText.Text = placeholder.Subtitle;
        SubtitleText.Visibility = placeholder.Subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BodyText.Text = placeholder.Body ?? "";
        BodyText.Visibility = placeholder.Body is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

using BenchBook.App.Services;
using BenchBook.Core;
using Microsoft.UI.Xaml;

namespace BenchBook.App;

/// <summary>Shown instead of the main window when the store can't be opened. Nothing is changed or recreated.</summary>
public sealed partial class LibraryErrorWindow : Window
{
    private readonly LibraryLocation _location;

    public LibraryErrorWindow(LibraryLocation location, string message)
    {
        _location = location;
        InitializeComponent();
        ErrorText.Text = message;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 420));
    }

    private void ShowLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_location.StorePath)) Shell.ShowInExplorer(_location.StorePath);
        else if (Directory.Exists(_location.AppFolder)) Shell.OpenFolder(_location.AppFolder);
    }
}

using BenchBook.App.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace BenchBook.App.Viewers;

/// <summary>Viewer windows remember their size and position, per kind of viewer.</summary>
internal static class WindowFrames
{
    public static void Restore(Window window, AppServices services, string kind, int width, int height)
    {
        if (services.Settings.WindowFrames.TryGetValue(kind, out var frame) && frame.Length == 4 && frame[2] > 200 && frame[3] > 150)
        {
            var rect = new RectInt32(frame[0], frame[1], frame[2], frame[3]);
            // Only if it would still be on a screen, e.g. after a monitor is unplugged.
            var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);
            if (area is not null)
            {
                window.AppWindow.MoveAndResize(rect);
                return;
            }
        }
        window.AppWindow.Resize(new SizeInt32(width, height));
    }

    public static void Save(Window window, AppServices services, string kind)
    {
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;
        var position = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        services.Settings.WindowFrames[kind] = [position.X, position.Y, size.Width, size.Height];
        services.SaveSettings();
    }
}

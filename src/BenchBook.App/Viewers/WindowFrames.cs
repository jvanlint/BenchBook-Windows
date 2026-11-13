using BenchBook.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace BenchBook.App.Viewers;

/// <summary>Viewer windows remember their size and position, per kind of viewer.</summary>
internal static class WindowFrames
{
    /// <summary>
    /// Brings a viewer window to the front once the click or key press that opened
    /// it has finished. Activated during a double-click, the window that was clicked
    /// takes the focus back when the click ends and covers the viewer.
    /// </summary>
    public static void Present(Window window)
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null || !queue.TryEnqueue(DispatcherQueuePriority.Low, () => Front(window))) Front(window);
    }

    /// <summary>
    /// Enter saves a note and returns to the picture; Shift+Enter starts a new line.
    /// </summary>
    public static void CommitOnEnter(Microsoft.UI.Xaml.Controls.TextBox field, Action commit)
    {
        field.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shift) return;
            e.Handled = true;
            commit();
        };
    }

    private static void Front(Window window)
    {
        window.Activate();
        window.AppWindow.MoveInZOrderAtTop();
    }

    // The last size and position each window had while it wasn't maximised or minimised.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, RectBox> Normal = [];

    private sealed class RectBox { public RectInt32 Rect; }

    /// <summary>
    /// Puts the window where it was last time: its size and position, and
    /// whether it was maximised. A frame that's no longer on any screen, e.g.
    /// after a monitor is unplugged, is ignored.
    /// </summary>
    public static void Restore(Window window, AppServices services, string kind, int width, int height)
    {
        var box = new RectBox();
        Normal.AddOrUpdate(window, box);
        var maximise = false;
        if (services.Settings.WindowFrames.TryGetValue(kind, out var frame) && frame.Length >= 4 && frame[2] > 200 && frame[3] > 150
            && DisplayArea.GetFromRect(new RectInt32(frame[0], frame[1], frame[2], frame[3]), DisplayAreaFallback.None) is not null)
        {
            window.AppWindow.MoveAndResize(new RectInt32(frame[0], frame[1], frame[2], frame[3]));
            maximise = frame.Length >= 5 && frame[4] == 1;
        }
        else
        {
            window.AppWindow.Resize(new SizeInt32(width, height));
        }
        box.Rect = new RectInt32(window.AppWindow.Position.X, window.AppWindow.Position.Y, window.AppWindow.Size.Width, window.AppWindow.Size.Height);
        if (maximise && window.AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();

        window.AppWindow.Changed += (appWindow, args) =>
        {
            if (!args.DidSizeChange && !args.DidPositionChange) return;
            if (appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
                box.Rect = new RectInt32(appWindow.Position.X, appWindow.Position.Y, appWindow.Size.Width, appWindow.Size.Height);
        };
    }

    /// <summary>Saves the window's normal size and position, and whether it's maximised.</summary>
    public static void Save(Window window, AppServices services, string kind)
    {
        var maximised = window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        var minimised = window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        var rect = Normal.TryGetValue(window, out var box) && box.Rect.Width > 0
            ? box.Rect
            : new RectInt32(window.AppWindow.Position.X, window.AppWindow.Position.Y, window.AppWindow.Size.Width, window.AppWindow.Size.Height);
        if (minimised && box is null) return;
        services.Settings.WindowFrames[kind] = [rect.X, rect.Y, rect.Width, rect.Height, maximised ? 1 : 0];
        services.SaveSettings();
    }
}

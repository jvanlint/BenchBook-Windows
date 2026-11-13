using BenchBook.App.Services;
using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Store;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BenchBook.App.Views;

/// <summary>Opening and editing a reference, the same from the gallery, search and tag lists.</summary>
internal static class ReferenceActions
{
    /// <summary>Opens a reference the way its kind opens (screens.md, "Opening"). True if it may have changed.</summary>
    public static async Task<bool> Open(XamlRoot root, AppServices services, ReferenceEntry entry)
    {
        var reference = entry.Reference;
        switch (reference.Kind)
        {
            case ReferenceKind.Image:
                Viewers.ImageViewerWindow.Show(services, entry);
                return false;
            case ReferenceKind.Markdown:
                Viewers.MarkdownWindow.Show(services, entry);
                return false;
            case ReferenceKind.Pdf:
                Viewers.PdfViewerWindow.Show(services, entry);
                return false;
            case ReferenceKind.Webpage:
                await Opener.OpenLink(reference.SourcePageUrl);
                return false;
            case ReferenceKind.VideoLink:
                await Opener.OpenLink(entry.Moments.Count > 0
                    ? VideoLink.PlaybackUrl(reference.SourcePageUrl ?? "", entry.Moments[0].OffsetSeconds)
                    : reference.SourcePageUrl);
                return false;
            case ReferenceKind.Note:
                return await EditNote(root, services, entry);
            default:
                return await EditDetails(root, services, entry);
        }
    }

    public static async Task OpenFile(XamlRoot root, AppServices services, ReferenceEntry entry)
    {
        if (entry.Asset is not { } asset || !await Opener.OpenInDefaultApp(services.Location, asset))
            await Message(root, "Couldn't Open the File",
                $"“{ReferenceText.FallbackTitle(entry)}” couldn't be opened. Its saved copy may be missing; Clean Up Now in Settings tidies the library.");
    }

    public static async Task<bool> EditNote(XamlRoot root, AppServices services, ReferenceEntry entry)
    {
        var dialog = new NoteEditorDialog(services, entry.Reference.ProjectId, entry) { XamlRoot = root };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task<bool> EditDetails(XamlRoot root, AppServices services, ReferenceEntry entry)
    {
        if (entry.Reference.Kind == ReferenceKind.Note) return await EditNote(root, services, entry);
        var dialog = new ReferenceDetailsDialog(services, entry) { XamlRoot = root };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task Message(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                MaxHeight = 360,
            },
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }
}

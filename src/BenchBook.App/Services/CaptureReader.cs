using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace BenchBook.App.Services;

/// <summary>
/// Turns a drop or the clipboard into captured items and imports them, the same
/// for the gallery and for a project in the sidebar.
/// </summary>
internal static class CaptureReader
{
    /// <summary>True for files, links, pictures or text from outside BenchBook.</summary>
    public static bool CanCapture(DataPackageView data) =>
        !data.Contains(AppServices.ReferencesDragFormat) && !data.Contains(AppServices.TagDragFormat)
        && (data.Contains(StandardDataFormats.StorageItems) || data.Contains(StandardDataFormats.WebLink)
            || data.Contains(StandardDataFormats.Bitmap) || data.Contains(StandardDataFormats.Text));

    /// <summary>Reads what was dropped or pasted and adds it to the project. Returns what to tell the user, if anything.</summary>
    public static async Task<CaptureReport?> CaptureAsync(AppServices services, DataPackageView data, string projectId)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "BenchBook Capture", Guid.NewGuid().ToString("N"));
        try
        {
            var (items, skipped) = await Read(data, temporary);
            if (items.Count == 0 && skipped.Count == 0) return null;
            return await ImportAsync(services, items, skipped, projectId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return new CaptureReport("An Item Couldn't Be Added", $"BenchBook couldn't read what was dropped or pasted ({error.Message}).");
        }
        finally
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>Imports the items, then fetches link details in the background. Saving never waits for the network.</summary>
    public static async Task<CaptureReport?> ImportAsync(AppServices services, IReadOnlyList<CapturedItem> items, IReadOnlyList<string> skipped, string projectId)
    {
        var outcomes = await Task.Run(() => services.Importer.Import(items, projectId));
        services.FetchLinkDetails(outcomes.OfType<CaptureOutcome.Added>()
            .Where(a => a.Reference.Kind is ReferenceKind.VideoLink or ReferenceKind.Webpage).Select(a => a.Reference.Id));
        return ReferenceImporter.Report(outcomes, skipped);
    }

    private static async Task<(List<CapturedItem> Items, List<string> Skipped)> Read(DataPackageView data, string temporary)
    {
        var items = new List<CapturedItem>();
        var skipped = new List<string>();
        if (data.Contains(StandardDataFormats.StorageItems))
        {
            foreach (var item in await data.GetStorageItemsAsync())
            {
                if (item is StorageFile file)
                {
                    var path = file.Path;
                    if (string.IsNullOrEmpty(path))
                    {
                        // A file with no path on disk, e.g. from a browser: copy it out first.
                        Directory.CreateDirectory(temporary);
                        var copy = await file.CopyAsync(await StorageFolder.GetFolderFromPathAsync(temporary), file.Name, NameCollisionOption.GenerateUniqueName);
                        path = copy.Path;
                    }
                    items.Add(new CapturedItem.File(path, file.Name));
                }
                else
                {
                    skipped.Add(item.Name);
                }
            }
        }
        else if (data.Contains(StandardDataFormats.WebLink))
        {
            var link = await data.GetWebLinkAsync();
            string? title = null;
            if (data.Contains(StandardDataFormats.Text))
            {
                var text = (await data.GetTextAsync()).Trim();
                if (text.Length > 0 && text != link.OriginalString && text.Length <= 300 && !text.Contains('\n')) title = text;
            }
            items.Add(new CapturedItem.Link(link.AbsoluteUri, title));
        }
        else if (data.Contains(StandardDataFormats.Bitmap))
        {
            // A picture with no file is saved as PNG.
            Directory.CreateDirectory(temporary);
            var name = $"Pasted Image {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png";
            var reference = await data.GetBitmapAsync();
            var folder = await StorageFolder.GetFolderFromPathAsync(temporary);
            var target = await folder.CreateFileAsync(name);
            using (var input = await reference.OpenReadAsync())
            using (var output = await target.OpenAsync(FileAccessMode.ReadWrite))
            {
                var decoder = await BitmapDecoder.CreateAsync(input);
                using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }
            items.Add(new CapturedItem.File(Path.Combine(temporary, name), name));
        }
        else if (data.Contains(StandardDataFormats.Text))
        {
            var text = await data.GetTextAsync();
            items.Add(ReferenceImporter.IsWebLink(text, out var url) && !text.Trim().Contains(' ')
                ? new CapturedItem.Link(url.AbsoluteUri)
                : new CapturedItem.Text(text));
        }
        return (items, skipped);
    }
}

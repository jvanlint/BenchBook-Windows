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
        !data.Contains(AppServices.ReferencesDragFormat) && !data.Contains(AppServices.TagDragFormat) && !data.Contains(AppServices.BuildPhotosDragFormat)
        && (data.Contains(StandardDataFormats.StorageItems) || data.Contains(StandardDataFormats.WebLink)
            || data.Contains(StandardDataFormats.Bitmap) || data.Contains(StandardDataFormats.Text));

    /// <summary>Reads what was dropped or pasted and adds it to the project. Returns what to tell the user, if anything.</summary>
    public static async Task<CaptureReport?> CaptureAsync(AppServices services, DataPackageView data, string projectId)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "BenchBook Capture", Guid.NewGuid().ToString("N"));
        try
        {
            var (items, skipped) = await Read(data, temporary, share: false);
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

    /// <summary>
    /// Reads what's being shared, copying every file into <paramref name="folder"/>
    /// under a new name, since a shared file may vanish when sharing ends. Files
    /// BenchBook can't save are skipped, so the share window can say so.
    /// </summary>
    public static Task<(List<CapturedItem> Items, List<string> Skipped)> ReadForShare(DataPackageView data, string folder) =>
        Read(data, folder, share: true);

    /// <summary>
    /// Reads files and pictures from a drop or the clipboard into <paramref name="temporary"/>
    /// where they have no file of their own, for Build Photos. Links and text aren't photos.
    /// </summary>
    public static async Task<(List<(string Path, string? Name)> Files, List<string> Skipped)> ReadPhotos(DataPackageView data, string temporary)
    {
        var (items, skipped) = await Read(data, temporary, share: false);
        var files = new List<(string, string?)>();
        foreach (var item in items)
        {
            if (item is CapturedItem.File file) files.Add((file.Path, file.OriginalFilename));
            else skipped.Add(item is CapturedItem.Link link ? link.Url : "Text");
        }
        return (files, skipped);
    }

    /// <summary>True for files or pictures from outside BenchBook, which can be build photos.</summary>
    public static bool CanCapturePhotos(DataPackageView data) =>
        CanCapture(data) && (data.Contains(StandardDataFormats.StorageItems) || data.Contains(StandardDataFormats.Bitmap));

    private static async Task<(List<CapturedItem> Items, List<string> Skipped)> Read(DataPackageView data, string temporary, bool share)
    {
        var items = new List<CapturedItem>();
        var skipped = new List<string>();
        // The page files came from, such as the page an image was dragged from, when the source says.
        var page = await PageOf(data);
        if (data.Contains(StandardDataFormats.StorageItems))
        {
            foreach (var item in await data.GetStorageItemsAsync())
            {
                if (item is StorageFile file)
                {
                    var path = file.Path;
                    if (share && !string.IsNullOrEmpty(path) && !FileInspector.LooksSaveable(path, file.Name))
                    {
                        skipped.Add(file.Name);
                        continue;
                    }
                    if (share || string.IsNullOrEmpty(path))
                    {
                        // A file with no path on disk, e.g. from a browser, is copied out first.
                        Directory.CreateDirectory(temporary);
                        var name = share ? Guid.NewGuid().ToString("N") + Path.GetExtension(file.Name) : file.Name;
                        var copy = await file.CopyAsync(await StorageFolder.GetFolderFromPathAsync(temporary), name, NameCollisionOption.GenerateUniqueName);
                        path = copy.Path;
                        if (share && !FileInspector.LooksSaveable(path, file.Name))
                        {
                            File.Delete(path);
                            skipped.Add(file.Name);
                            continue;
                        }
                    }
                    items.Add(new CapturedItem.File(path, file.Name, PageUrl: page));
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
            var pageTitle = string.IsNullOrWhiteSpace(data.Properties.Title) ? null : data.Properties.Title.Trim();
            var title = pageTitle;
            if (data.Contains(StandardDataFormats.Text))
            {
                var text = (await data.GetTextAsync()).Trim();
                if (text.Length > 0 && text != link.OriginalString && text != pageTitle)
                {
                    // Text that isn't the page's title is a snippet from the page, with the page
                    // as its source, as on the Mac (ItemProviderReader.swift). When the title
                    // isn't given separately, one short line is taken to be the title.
                    if (pageTitle is null && text.Length <= 300 && !text.Contains('\n')) title = text;
                    else
                    {
                        items.Add(new CapturedItem.Text(text, link.AbsoluteUri, pageTitle));
                        return (items, skipped);
                    }
                }
            }
            items.Add(new CapturedItem.Link(link.AbsoluteUri, title));
        }
        else if (data.Contains(StandardDataFormats.Bitmap))
        {
            // A picture with no file is saved as PNG.
            Directory.CreateDirectory(temporary);
            var shown = $"{(share ? "Shared" : "Pasted")} Image {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png";
            var name = share ? Guid.NewGuid().ToString("N") + ".png" : shown;
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
            items.Add(new CapturedItem.File(Path.Combine(temporary, name), shown, PageUrl: page));
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

    /// <summary>
    /// The web page the content came from: the page a share names, else the
    /// SourceURL in the HTML format browsers put beside a dragged or copied image.
    /// </summary>
    private static async Task<string?> PageOf(DataPackageView data)
    {
        if (data.Properties.ContentSourceWebLink is { } link && ReferenceImporter.IsWebLink(link.OriginalString, out var shared)) return shared.AbsoluteUri;
        if (!data.Contains(StandardDataFormats.Html)) return null;
        try
        {
            var html = await data.GetHtmlFormatAsync();
            // The clipboard's HTML format starts with "Name:value" lines, one of them "SourceURL:https://…".
            foreach (var line in html.Split('\n').TakeWhile(l => !l.TrimStart().StartsWith('<')))
                if (line.StartsWith("SourceURL:", StringComparison.OrdinalIgnoreCase) && ReferenceImporter.IsWebLink(line["SourceURL:".Length..].Trim(), out var source))
                    return source.AbsoluteUri;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // No page, then.
        }
        return null;
    }
}

using BenchBook.Core.Capture;
using BenchBook.Core.Links;
using BenchBook.Core.Model;
using BenchBook.Core.Store;

namespace BenchBook.Core.Paints;

public static class AftermarketKinds
{
    public static readonly IReadOnlyList<string> Types = ["decals", "resin", "photoEtch", "printed3D", "masks", "metal", "wheels", "figures", "other"];

    /// <summary>In the order a part usually moves through a build.</summary>
    public static readonly IReadOnlyList<string> Statuses = ["considering", "ordered", "inStash", "using", "used", "notUsing"];

    public static string TypeName(string type) => type switch
    {
        "decals" => "Decals",
        "resin" => "Resin",
        "photoEtch" => "Photo-etch",
        "printed3D" => "3D printed",
        "masks" => "Masks",
        "metal" => "Metal parts",
        "wheels" => "Wheels",
        "figures" => "Figures",
        "other" => "Other",
        _ => type,
    };

    public static string StatusName(string status) => status switch
    {
        "considering" => "Considering",
        "ordered" => "Ordered",
        "inStash" => "In stash",
        "using" => "Using",
        "used" => "Used",
        "notUsing" => "Not using",
        _ => status,
    };

    /// <summary>Unknown statuses sort last.</summary>
    public static int StatusRank(string status) => Statuses.ToList().IndexOf(status) is >= 0 and var i ? i : Statuses.Count;

    /// <summary>By status in the format's order, then by name.</summary>
    public static List<AftermarketItem> Sorted(IEnumerable<AftermarketItem> items) =>
        items.OrderBy(i => StatusRank(i.Status)).ThenBy(i => i.ProductName, NaturalComparer.Instance).ToList();

    /// <summary>e.g. "Galaxy Models · G48-012".</summary>
    public static string SecondaryLine(AftermarketItem item) =>
        string.Join(" · ", new[] { item.Manufacturer, item.ProductNumber ?? "" }.Where(s => s.Length > 0));
}

/// <summary>A part being created or edited. Nothing is stored until the user saves.</summary>
public sealed record AftermarketDraft
{
    public string ProductName { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string ProductNumber { get; init; } = "";
    public string Type { get; init; } = "decals";
    public string Status { get; init; } = "considering";
    public string ScaleCompatibility { get; init; } = "";
    public string ProductUrl { get; init; } = "";
    public string Notes { get; init; } = "";

    public bool HasValidName => ProductName.Trim().Length > 0;

    /// <summary>The product page, with "https://" added if it was left off. Null when empty or not a web address.</summary>
    public Uri? ParsedUrl
    {
        get
        {
            var text = ProductUrl.Trim();
            if (text.Length == 0) return null;
            if (!text.Contains("://")) text = "https://" + text;
            return Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" && url.Host.Contains('.') ? url : null;
        }
    }

    public bool HasValidUrl => ProductUrl.Trim().Length == 0 || ParsedUrl is not null;

    /// <summary>A new part for a project, starting with the project's scale.</summary>
    public static AftermarketDraft For(Project project) => new() { ScaleCompatibility = project.Scale };

    public static AftermarketDraft From(AftermarketItem item) => new()
    {
        ProductName = item.ProductName, Manufacturer = item.Manufacturer, ProductNumber = item.ProductNumber ?? "",
        Type = item.Type, Status = item.Status, ScaleCompatibility = item.ScaleCompatibility ?? "",
        ProductUrl = item.ProductUrl ?? "", Notes = item.Notes ?? "",
    };
}

public sealed class AftermarketException(string message) : Exception(message)
{
    public static AftermarketException BlankName() => new("A part needs a name.");
    public static AftermarketException InvalidUrl() => new("The product page isn't a web address.");
    public static AftermarketException NotAnImage(string name) => new($"“{name}” isn't an image BenchBook can use.");
    public static AftermarketException NoPageImage() => new("The product page doesn't offer a picture BenchBook can use.");
}

/// <summary>A project's aftermarket parts and their pictures, with clean-up of stored files.</summary>
public static class AftermarketService
{
    public static AftermarketItem Create(LibraryStore store, string projectId, AftermarketDraft draft)
    {
        Validate(draft);
        var now = IsoDate.Now();
        var item = new AftermarketItem
        {
            Id = Ids.New(), CreatedAt = now, UpdatedAt = now, ProjectId = projectId,
            ProductName = "", Manufacturer = "", Type = draft.Type, Status = draft.Status,
        };
        Apply(draft, item);
        store.InsertAftermarketItem(item);
        return item;
    }

    public static AftermarketItem Update(LibraryStore store, AftermarketItem item, AftermarketDraft draft)
    {
        Validate(draft);
        if (Tidy(draft) == Tidy(AftermarketDraft.From(item))) return item;
        Apply(draft, item);
        item.UpdatedAt = IsoDate.Now();
        store.UpdateAftermarketItem(item);
        return item;
    }

    public static void SetStatus(LibraryStore store, IEnumerable<AftermarketItem> items, string status)
    {
        foreach (var item in items.Where(i => i.Status != status))
        {
            item.Status = status;
            item.UpdatedAt = IsoDate.Now();
            store.UpdateAftermarketItem(item);
        }
    }

    /// <summary>Deletes the parts, then their pictures if nothing else uses them.</summary>
    public static void Delete(LibraryStore store, LibraryLocation location, IReadOnlyCollection<string> ids)
    {
        foreach (var asset in store.DeleteAftermarketItems(ids)) ReferenceService.RemoveAssetIfUnused(store, location, asset);
    }

    /// <summary>
    /// Copies an image file into the library as the part's picture, replacing
    /// any earlier one. The file is checked by its contents, like any capture.
    /// </summary>
    public static void SetImage(IStoreAccess store, LibraryLocation location, AftermarketItem item, string path, string displayName, Func<string, bool> pdfHasPages)
    {
        var inspected = FileInspector.Inspect(path, displayName, pdfHasPages);
        if (inspected.Kind != ReferenceKind.Image) throw AftermarketException.NotAnImage(displayName);

        var now = IsoDate.Now();
        var assetId = Ids.New();
        var relativePath = $"Originals/{assetId}.{inspected.Extension}";
        var destination = location.FullPath(relativePath)!;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = Path.Combine(Path.GetDirectoryName(destination)!, LibraryPaths.PartialPrefix + Ids.New());
        File.Copy(path, partial);
        File.Move(partial, destination);

        var asset = new Asset
        {
            Id = assetId, CreatedAt = now, UpdatedAt = now, OriginalRelativePath = relativePath, OriginalFilename = displayName,
            MimeType = inspected.MimeType, ByteCount = inspected.ByteCount, Sha256 = inspected.Sha256,
            PixelWidth = inspected.PixelWidth, PixelHeight = inspected.PixelHeight,
        };
        var previous = item.ImageAssetId;
        item.ImageAssetId = assetId;
        item.UpdatedAt = now;
        try
        {
            store.With(s => { s.SetAftermarketImage(item, asset); return 0; });
        }
        catch
        {
            item.ImageAssetId = previous;
            try { File.Delete(destination); } catch (IOException) { }
            throw;
        }
        if (previous is not null) store.With(s => { ReferenceService.RemoveAssetIfUnused(s, location, previous); return 0; });
    }

    public static void RemoveImage(LibraryStore store, LibraryLocation location, AftermarketItem item)
    {
        if (item.ImageAssetId is not { } previous) return;
        item.ImageAssetId = null;
        item.UpdatedAt = IsoDate.Now();
        store.SetAftermarketImage(item, null);
        ReferenceService.RemoveAssetIfUnused(store, location, previous);
    }

    /// <summary>
    /// Downloads the picture a product page offers for link previews into a file
    /// in <paramref name="folder"/>, ready for <see cref="SetImage"/>. Site icons
    /// aren't pictures of the product, so they're not used.
    /// </summary>
    public static async Task<string> DownloadPageImage(LinkFetcher fetcher, IImageScaler images, Uri page, string folder)
    {
        PageInfo info;
        try { info = await PageDetails.Fetch(fetcher, page); }
        catch (Exception error) when (error is LinkNotFoundException or LinkUnavailableException) { throw AftermarketException.NoPageImage(); }
        if (await PageDetails.Preview(fetcher, images, info, allowIcons: false) is not { } preview) throw AftermarketException.NoPageImage();
        var isPng = preview.Data.Length > 4 && preview.Data[0] == 0x89 && preview.Data[1] == 0x50;
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"Product Image.{(isPng ? "png" : "jpg")}");
        await File.WriteAllBytesAsync(file, preview.Data);
        return file;
    }

    private static void Validate(AftermarketDraft draft)
    {
        if (!draft.HasValidName) throw AftermarketException.BlankName();
        if (!draft.HasValidUrl) throw AftermarketException.InvalidUrl();
    }

    private static AftermarketDraft Tidy(AftermarketDraft d) => d with
    {
        ProductName = d.ProductName.Trim(), Manufacturer = d.Manufacturer.Trim(), ProductNumber = d.ProductNumber.Trim(),
        ScaleCompatibility = d.ScaleCompatibility.Trim(), ProductUrl = d.ParsedUrl?.AbsoluteUri ?? "", Notes = d.Notes.Trim(),
    };

    private static void Apply(AftermarketDraft draft, AftermarketItem item)
    {
        var t = Tidy(draft);
        item.ProductName = t.ProductName;
        item.Manufacturer = t.Manufacturer;
        item.ProductNumber = t.ProductNumber.Length == 0 ? null : t.ProductNumber;
        item.Type = t.Type;
        item.Status = t.Status;
        item.ScaleCompatibility = t.ScaleCompatibility.Length == 0 ? null : t.ScaleCompatibility;
        item.ProductUrl = t.ProductUrl.Length == 0 ? null : t.ProductUrl;
        item.Notes = t.Notes.Length == 0 ? null : t.Notes;
    }
}

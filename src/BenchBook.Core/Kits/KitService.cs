using System.Globalization;
using BenchBook.Core.Capture;
using BenchBook.Core.Model;
using BenchBook.Core.Paints;
using BenchBook.Core.Store;

namespace BenchBook.Core.Kits;

/// <summary>How kits read (Kit+Display.swift).</summary>
public static class KitText
{
    public static string StatusName(string status) => Shown(status) switch
    {
        KitStatus.Opened => "Opened",
        KitStatus.Wip => "WIP",
        KitStatus.Built => "Built",
        _ => "BNIB",
    };

    /// <summary>What BNIB and WIP stand for, for tooltips and screen readers.</summary>
    public static string LongName(string status) => Shown(status) switch
    {
        KitStatus.Opened => "Opened",
        KitStatus.Wip => "Work in Progress",
        KitStatus.Built => "Built",
        _ => "Brand New in Box",
    };

    /// <summary>The status as shown: unknown values read as BNIB, and are kept until the user picks one.</summary>
    public static string Shown(string status) => KitStatus.All.Contains(status) ? status : KitStatus.Bnib;

    /// <summary>"BNIB", or "Not Owned" for a kit that isn't owned.</summary>
    public static string StatusOrNotOwned(Kit kit) => kit.Owned ? StatusName(kit.Status) : "Not Owned";

    /// <summary>1 to 5, or null. Other stored values read as no rating.</summary>
    public static int? Stars(Kit kit) => kit.Rating is >= 1 and <= 5 ? kit.Rating : null;

    /// <summary>e.g. "Italeri 1:72 Super Cobra (1234)".</summary>
    public static string DisplayName(Kit kit) =>
        string.Join(" ", new[] { kit.Brand, kit.Scale, kit.Name }.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)))
        + (string.IsNullOrWhiteSpace(kit.Number) ? "" : $" ({kit.Number.Trim()})");

    /// <summary>"Italeri · 1:72 · 1234", leaving out empty parts.</summary>
    public static string Details(Kit kit) =>
        string.Join(" · ", new[] { kit.Brand, kit.Scale, kit.Number }.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>"Italeri · 1234", for a project's Kits tab, where scale has its own column.</summary>
    public static string BrandAndNumber(Kit kit) =>
        string.Join(" · ", new[] { kit.Brand, kit.Number }.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>"3 kits", "1 kit".</summary>
    public static string Count(int count) => $"{count:N0} {(count == 1 ? "kit" : "kits")}";

    /// <summary>BNIB, Opened, WIP, Built, then kits that aren't owned.</summary>
    public static int StatusSortKey(Kit kit) => kit.Owned ? KitStatus.All.ToList().IndexOf(Shown(kit.Status)) : KitStatus.All.Count;

    /// <summary>Highest rating first, unrated last.</summary>
    public static int RatingSortKey(Kit kit) => 6 - (Stars(kit) ?? 0);

    /// <summary>By brand, name and number, then the order they were added, so copies stay together.</summary>
    public static List<Kit> Sorted(IEnumerable<Kit> kits) =>
        kits.OrderBy(k => k.Brand, NaturalComparer.Instance)
            .ThenBy(k => k.Name, NaturalComparer.Instance)
            .ThenBy(k => k.Number ?? "", NaturalComparer.Instance)
            .ThenBy(k => k.CreatedAt)
            .ToList();

    /// <summary>The link's host, for "Open www.scalemates.com".</summary>
    public static string? LinkHost(Kit kit) =>
        Uri.TryCreate(kit.Link, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? url.Host : null;
}

/// <summary>When two kit entries are copies of the same kit (behaviour.md, "Same kit", stored).</summary>
public static class KitIdentity
{
    public static bool IsSameKit(string brand, string? number, string? scale, string? link, Kit other) =>
        IsSameKit(brand, number, scale, link, other.Brand, other.Number, other.Scale, other.Link);

    public static bool IsSameKit(Kit kit, Kit other) => IsSameKit(kit.Brand, kit.Number, kit.Scale, kit.Link, other);

    public static bool IsSameKit(string brand, string? number, string? scale, string? link,
        string otherBrand, string? otherNumber, string? otherScale, string? otherLink)
    {
        if (ScalematesKey(link) is { } key && key == ScalematesKey(otherLink)) return true;
        if (Fold(number) is not { Length: > 0 } folded) return false;
        return Fold(brand) == Fold(otherBrand) && folded == Fold(otherNumber) && (Fold(scale) ?? "") == (Fold(otherScale) ?? "");
    }

    /// <summary>
    /// A Scalemates address without "www.", query or trailing slash, in lower
    /// case: "scalemates.com/kits/italeri-1234--100". Null for other sites.
    /// </summary>
    public static string? ScalematesKey(string? link)
    {
        if (link is null || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var url) || url.Host.Length == 0) return null;
        var host = url.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host != "scalemates.com" && !host.EndsWith(".scalemates.com", StringComparison.Ordinal)) return null;
        var path = Uri.UnescapeDataString(url.AbsolutePath).ToLowerInvariant().TrimEnd('/');
        return host + path;
    }

    /// <summary>Lower case, with every whitespace character removed.</summary>
    public static string? Fold(string? text) =>
        text is null ? null : new string(text.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());
}

/// <summary>Amounts of money typed by the user or read from a file (behaviour.md, "Prices", stored).</summary>
public static class KitPrice
{
    /// <summary>
    /// Reads "25", "25.00", "25,00", "$18.50", "1,250.00" or "1.250,00". Null for
    /// empty, negative or unreadable text.
    /// </summary>
    public static decimal? Parse(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Contains('-')) return null;
        var kept = new string(trimmed.Where(c => c is (>= '0' and <= '9') or '.' or ',').ToArray());
        if (!kept.Any(char.IsAsciiDigit)) return null;

        var kinds = kept.Where(c => c is '.' or ',').Distinct().ToList();
        char? point;
        if (kinds.Count == 2)
        {
            point = kept.Last(c => c is '.' or ',');
        }
        else if (kinds.Count == 1)
        {
            var only = kinds[0];
            var parts = kept.Split(only);
            var groups = parts.Length > 2
                || (parts.Length == 2 && parts[0].Length is >= 1 and <= 3 && parts[0] != "0" && parts[1].Length == 3);
            point = groups ? null : only;
        }
        else
        {
            point = null;
        }

        var plain = new string(kept.Select(c => char.IsAsciiDigit(c) ? c : c == point ? '.' : '\0').Where(c => c != '\0').ToArray());
        // A lone "." with no digits after it, such as "25.", still reads as 25.
        return decimal.TryParse(plain, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ? amount : null;
    }

    /// <summary>"18.50": for files and backups.</summary>
    public static string Plain(decimal amount) => Money.Write(amount);

    /// <summary>In the device's currency format, e.g. "$18.50".</summary>
    public static string Display(decimal amount) => amount.ToString("C", CultureInfo.CurrentCulture);
}

/// <summary>The kit editor's fields, as typed. Nothing is saved until Save.</summary>
public sealed record KitDraft
{
    public string Brand { get; init; } = "";
    public string Name { get; init; } = "";
    public string Scale { get; init; } = "";
    public string Number { get; init; } = "";
    public bool Owned { get; init; } = true;
    /// <summary>The status as shown; a kit's unknown stored status shows as BNIB.</summary>
    public string Status { get; init; } = KitStatus.Bnib;
    public int? Stars { get; init; }
    public string Link { get; init; } = "";
    public string PricePaid { get; init; } = "";
    public bool ForSale { get; init; }
    public string SalePrice { get; init; } = "";
    public string Notes { get; init; } = "";

    public static KitDraft From(Kit kit) => new()
    {
        Brand = kit.Brand, Name = kit.Name, Scale = kit.Scale ?? "", Number = kit.Number ?? "",
        Owned = kit.Owned, Status = KitText.Shown(kit.Status), Stars = KitText.Stars(kit), Link = kit.Link ?? "",
        PricePaid = kit.PricePaid is { } paid ? KitPrice.Plain(paid) : "", ForSale = kit.ForSale,
        SalePrice = kit.SalePrice is { } sale ? KitPrice.Plain(sale) : "", Notes = kit.Notes ?? "",
    };

    public bool HasValidBrand => Brand.Trim().Length > 0;
    public bool HasValidName => Name.Trim().Length > 0;
    public string? ParsedLink => WebAddress.Parse(Link);
    public bool HasValidLink => Link.Trim().Length == 0 || ParsedLink is not null;
    public bool HasValidPricePaid => PricePaid.Trim().Length == 0 || KitPrice.Parse(PricePaid) is not null;
    public bool HasValidSalePrice => SalePrice.Trim().Length == 0 || KitPrice.Parse(SalePrice) is not null;
    public bool IsValid => HasValidBrand && HasValidName && HasValidLink && HasValidPricePaid && HasValidSalePrice;
}

/// <summary>A web address as typed: "https://" is added if it was left off (WebAddress.swift).</summary>
public static class WebAddress
{
    /// <summary>Null when the text is empty or isn't an http or https address with a domain.</summary>
    public static string? Parse(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) return null;
        if (!trimmed.Contains("://")) trimmed = "https://" + trimmed;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" && url.Host.Contains('.')
            ? url.AbsoluteUri
            : null;
    }
}

public sealed class KitException(string message) : Exception(message)
{
    public static KitException BlankBrand() => new("A kit needs a brand.");
    public static KitException BlankName() => new("A kit needs a name.");
    public static KitException InvalidLink() => new("The link isn't a web address.");
    public static KitException InvalidPrice() => new("Enter prices as numbers, such as 18.50.");
}

/// <summary>Which kits the Kits screen shows.</summary>
public sealed record KitFilter
{
    public const string All = "all";
    public const string Owned = "owned";
    public const string NotOwned = "notOwned";
    public const string ForSale = "forSale";
    public const string Unallocated = "unallocated";
    public const string Needed = "needed";

    public static readonly IReadOnlyList<string> Ownerships = [All, Owned, NotOwned, ForSale, Unallocated, Needed];

    public static string OwnershipName(string ownership) => ownership switch
    {
        Owned => "Owned",
        NotOwned => "Not Owned",
        ForSale => "For Sale",
        Unallocated => "Not in a Project",
        Needed => "Needed",
        _ => "All Kits",
    };

    /// <summary>A <see cref="PaintFilterOption.Key"/>; null shows every brand.</summary>
    public string? Brand { get; init; }
    public string? Scale { get; init; }
    /// <summary>Only owned kits have a status, so choosing one shows owned kits only.</summary>
    public string? Status { get; init; }
    public string Ownership { get; init; } = All;

    public bool IsActive => Brand is not null || Scale is not null || Status is not null || Ownership != All;

    /// <param name="projects">Every project by id, for Needed.</param>
    public bool Includes(Kit kit, IReadOnlyDictionary<string, Project> projects) =>
        (Brand is null || PaintIdentity.Words(kit.Brand) == Brand)
        && (Scale is null || PaintIdentity.Words(kit.Scale ?? "") == Scale)
        && (Status is null || (kit.Owned && KitText.Shown(kit.Status) == Status))
        && Ownership switch
        {
            Owned => kit.Owned,
            NotOwned => !kit.Owned,
            ForSale => kit.ForSale,
            Unallocated => kit.ProjectId is null,
            Needed => KitRules.IsNeeded(kit, projects),
            _ => true,
        };
}

/// <summary>Kits in projects (behaviour.md, stored).</summary>
public static class KitRules
{
    /// <summary>
    /// Moves an owned kit forward to match its project: In Progress makes a BNIB
    /// or Opened kit WIP, and Completed makes it Built. Never back; an unknown
    /// stored status changes only for Completed. True if it changed.
    /// </summary>
    public static bool Follow(Kit kit, string? projectStatus, DateTimeOffset now)
    {
        if (!kit.Owned) return false;
        string target;
        switch (projectStatus ?? ProjectStatus.Researching)
        {
            case ProjectStatus.InProgress:
                if (kit.Status != KitStatus.Bnib && kit.Status != KitStatus.Opened) return false;
                target = KitStatus.Wip;
                break;
            case ProjectStatus.Completed:
                if (kit.Status == KitStatus.Built) return false;
                target = KitStatus.Built;
                break;
            default:
                return false;
        }
        kit.Status = target;
        kit.UpdatedAt = now;
        return true;
    }

    /// <summary>Still to buy for a build: not owned, and in a project that isn't Completed.</summary>
    public static bool IsNeeded(Kit kit, IReadOnlyDictionary<string, Project> projects) =>
        !kit.Owned && kit.ProjectId is { } id && projects.TryGetValue(id, out var project) && project.Status != ProjectStatus.Completed;
}

/// <summary>
/// The Kits catalogue: adding and editing kits, copies, status and sale changes,
/// pictures and deleting, with clean-up of stored files (KitService.swift).
/// Editing a kit doesn't change its project's last updated date; adding a kit
/// to a project, or removing it, does.
/// </summary>
public static class KitService
{
    /// <summary>A new kit in the catalogue, and in the project if one is given.</summary>
    public static Kit Create(LibraryStore store, KitDraft draft, Project? project = null)
    {
        Validate(draft);
        var now = IsoDate.Now();
        var kit = new Kit
        {
            Id = Ids.New(), CreatedAt = now, UpdatedAt = now,
            Brand = "", Name = "", Owned = true, Status = KitStatus.Bnib, ForSale = false,
        };
        Apply(draft, kit);
        if (project is not null)
        {
            kit.ProjectId = project.Id;
            KitRules.Follow(kit, project.Status, now);
            kit.UpdatedAt = now;
        }
        store.SaveKits([kit], project is null ? null : [project.Id]);
        return kit;
    }

    /// <summary>Saves the draft over the kit. Nothing changes, not even the date, if the draft is the same.</summary>
    public static Kit Update(LibraryStore store, Kit kit, KitDraft draft)
    {
        Validate(draft);
        if (Tidy(draft) == Tidy(KitDraft.From(kit))) return kit;
        Apply(draft, kit);
        kit.UpdatedAt = IsoDate.Now();
        store.SaveKits([kit]);
        return kit;
    }

    /// <summary>
    /// Another box of the same kit: the same brand, name, scale, number, link and
    /// picture, owned and BNIB, with no price, rating, sale details or notes.
    /// </summary>
    public static Kit AddCopy(LibraryStore store, Kit kit)
    {
        var now = IsoDate.Now();
        var copy = new Kit
        {
            Id = Ids.New(), CreatedAt = now, UpdatedAt = now,
            Brand = kit.Brand, Name = kit.Name, Scale = kit.Scale, Number = kit.Number, Link = kit.Link,
            PictureAssetId = kit.PictureAssetId, Owned = true, Status = KitStatus.Bnib, ForSale = false,
        };
        store.SaveKits([copy]);
        return copy;
    }

    /// <summary>The other entries for the same kit, oldest first, leaving out the one being edited.</summary>
    public static List<Kit> Copies(IEnumerable<Kit> kits, KitDraft draft, Kit? excluding = null) =>
        kits.Where(k => excluding is null || !Ids.Comparer.Equals(k.Id, excluding.Id))
            .Where(k => KitIdentity.IsSameKit(draft.Brand, NullIfBlank(draft.Number), NullIfBlank(draft.Scale), draft.ParsedLink, k))
            .OrderBy(k => k.CreatedAt)
            .ToList();

    /// <summary>"You already have 2 of this kit. This adds another." or "You have 1 more of this kit."; null for none.</summary>
    public static string? CopiesMessage(int copies, bool editing) => copies == 0
        ? null
        : editing
            ? $"You have {copies:N0} more of this kit."
            : $"You already have {copies:N0} of this kit. This adds another.";

    public static void SetStatus(LibraryStore store, IEnumerable<Kit> kits, string status) =>
        Change(store, kits.Where(k => k.Status != status), k => k.Status = status);

    public static void SetOwned(LibraryStore store, IEnumerable<Kit> kits, bool owned) =>
        Change(store, kits.Where(k => k.Owned != owned), k => k.Owned = owned);

    /// <summary>A needed kit has been bought: it's owned, and BNIB.</summary>
    public static void MarkBought(LibraryStore store, IEnumerable<Kit> kits) =>
        Change(store, kits.Where(k => !k.Owned), k => { k.Owned = true; k.Status = KitStatus.Bnib; });

    /// <summary>Turning For Sale off keeps the sale price.</summary>
    public static void SetForSale(LibraryStore store, IEnumerable<Kit> kits, bool forSale) =>
        Change(store, kits.Where(k => k.ForSale != forSale), k => k.ForSale = forSale);

    /// <summary>Kits that aren't in any project, which a project can use.</summary>
    public static List<Kit> Unallocated(LibraryStore store) => store.Kits().Where(k => k.ProjectId is null).ToList();

    /// <summary>Adds the kits to the project. A kit in another project moves to this one; both are updated.</summary>
    public static void Add(LibraryStore store, IEnumerable<Kit> kits, Project project)
    {
        var now = IsoDate.Now();
        var changed = new List<Kit>();
        var touched = new List<string?>();
        foreach (var kit in kits.Where(k => !Ids.Comparer.Equals(k.ProjectId, project.Id)))
        {
            touched.Add(kit.ProjectId);
            kit.ProjectId = project.Id;
            KitRules.Follow(kit, project.Status, now);
            kit.UpdatedAt = now;
            changed.Add(kit);
        }
        if (changed.Count == 0) return;
        touched.Add(project.Id);
        store.SaveKits(changed, touched);
    }

    /// <summary>Takes the kits out of their projects. They stay in the catalogue, in no project, with their status.</summary>
    public static void Remove(LibraryStore store, IEnumerable<Kit> kits)
    {
        var now = IsoDate.Now();
        var changed = kits.Where(k => k.ProjectId is not null).ToList();
        if (changed.Count == 0) return;
        var touched = changed.Select(k => k.ProjectId).ToList();
        foreach (var kit in changed)
        {
            kit.ProjectId = null;
            kit.UpdatedAt = now;
        }
        store.SaveKits(changed, touched);
    }

    /// <summary>
    /// "This removes the kit and its picture from BenchBook. This can't be
    /// undone.", starting with the projects that use them.
    /// </summary>
    public static (string Title, string Message) DeletionMessage(IReadOnlyList<Kit> kits, IReadOnlyDictionary<string, Project> projects)
    {
        var title = kits.Count == 1 ? $"Delete “{KitText.DisplayName(kits[0])}”?" : $"Delete {kits.Count:N0} Kits?";
        var names = kits.Select(k => k.ProjectId is { } id && projects.TryGetValue(id, out var p) ? p.Name : null)
            .OfType<string>().Distinct().OrderBy(n => n, NaturalComparer.Instance).Select(n => $"“{n}”").ToList();
        var used = names.Count switch
        {
            0 => "",
            1 when kits.Count == 1 => $"It's used by the project {names[0]}. ",
            1 => $"They're used by the project {names[0]}. ",
            _ => $"{(kits.Count == 1 ? "It's" : "They're")} used by the projects {TextList.List(names)}. ",
        };
        var picture = kits.Count == 1 ? "the kit and its picture" : "the kits and their pictures";
        return (title, $"{used}This removes {picture} from BenchBook. This can't be undone.");
    }

    /// <summary>Deletes the kits, then their pictures if nothing else uses them.</summary>
    public static void Delete(LibraryStore store, LibraryLocation location, IReadOnlyCollection<Kit> kits)
    {
        // As on the Mac, deleting a kit doesn't change its project's date.
        var assets = store.DeleteKits(kits.Select(k => k.Id).ToList());
        foreach (var asset in assets) ReferenceService.RemoveAssetIfUnused(store, location, asset);
    }

    /// <summary>
    /// Copies an image file into the library as the kit's box art, replacing any
    /// earlier one. Other copies of the kit keep their picture.
    /// </summary>
    public static void SetPicture(IStoreAccess store, LibraryLocation location, Kit kit, string path, string displayName, Func<string, bool> pdfHasPages)
    {
        var asset = PictureImport.Import(location, path, displayName, pdfHasPages);
        var previous = kit.PictureAssetId;
        kit.PictureAssetId = asset.Id;
        kit.UpdatedAt = IsoDate.Now();
        try
        {
            store.With(s => { s.SaveKits([kit], asset: asset); return 0; });
        }
        catch
        {
            kit.PictureAssetId = previous;
            PictureImport.Discard(location, asset);
            throw;
        }
        if (previous is not null) store.With(s => { ReferenceService.RemoveAssetIfUnused(s, location, previous); return 0; });
    }

    public static void RemovePicture(LibraryStore store, LibraryLocation location, Kit kit)
    {
        if (kit.PictureAssetId is not { } previous) return;
        kit.PictureAssetId = null;
        kit.UpdatedAt = IsoDate.Now();
        store.SaveKits([kit]);
        ReferenceService.RemoveAssetIfUnused(store, location, previous);
    }

    private static void Change(LibraryStore store, IEnumerable<Kit> kits, Action<Kit> change)
    {
        var now = IsoDate.Now();
        var changed = kits.ToList();
        foreach (var kit in changed)
        {
            change(kit);
            kit.UpdatedAt = now;
        }
        if (changed.Count > 0) store.SaveKits(changed);
    }

    private static void Validate(KitDraft draft)
    {
        if (!draft.HasValidBrand) throw KitException.BlankBrand();
        if (!draft.HasValidName) throw KitException.BlankName();
        if (!draft.HasValidLink) throw KitException.InvalidLink();
        if (!draft.HasValidPricePaid || !draft.HasValidSalePrice) throw KitException.InvalidPrice();
    }

    private static KitDraft Tidy(KitDraft d) => d with
    {
        Brand = d.Brand.Trim(), Name = d.Name.Trim(), Scale = d.Scale.Trim(), Number = d.Number.Trim(),
        Link = d.ParsedLink ?? "", Notes = d.Notes.Trim(),
        PricePaid = KitPrice.Parse(d.PricePaid) is { } paid ? KitPrice.Plain(paid) : "",
        SalePrice = KitPrice.Parse(d.SalePrice) is { } sale ? KitPrice.Plain(sale) : "",
        Stars = d.Stars is >= 1 and <= 5 ? d.Stars : null,
    };

    private static void Apply(KitDraft draft, Kit kit)
    {
        kit.Brand = draft.Brand.Trim();
        kit.Name = draft.Name.Trim();
        kit.Scale = NullIfBlank(draft.Scale);
        kit.Number = NullIfBlank(draft.Number);
        kit.Owned = draft.Owned;
        // Leaves an unknown stored status alone unless the user chose another.
        if (draft.Status != KitText.Shown(kit.Status)) kit.Status = draft.Status;
        kit.Rating = draft.Stars is >= 1 and <= 5 ? draft.Stars : null;
        kit.Link = draft.ParsedLink;
        kit.PricePaid = KitPrice.Parse(draft.PricePaid);
        kit.ForSale = draft.ForSale;
        kit.SalePrice = KitPrice.Parse(draft.SalePrice);
        kit.Notes = NullIfBlank(draft.Notes);
    }

    private static string? NullIfBlank(string text) => text.Trim() is { Length: > 0 } t ? t : null;
}

/// <summary>Short lists in sentences.</summary>
public static class TextList
{
    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string List(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}

/// <summary>Copies an image into the library as a new asset, as the aftermarket and kit editors do.</summary>
public static class PictureImport
{
    public static Asset Import(LibraryLocation location, string path, string displayName, Func<string, bool> pdfHasPages)
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
        return new Asset
        {
            Id = assetId, CreatedAt = now, UpdatedAt = now, OriginalRelativePath = relativePath, OriginalFilename = displayName,
            MimeType = inspected.MimeType, ByteCount = inspected.ByteCount, Sha256 = inspected.Sha256,
            PixelWidth = inspected.PixelWidth, PixelHeight = inspected.PixelHeight,
        };
    }

    /// <summary>Removes the file of an asset that was never saved.</summary>
    public static void Discard(LibraryLocation location, Asset asset)
    {
        try { if (location.FullPath(asset.OriginalRelativePath) is { } file) File.Delete(file); }
        catch (IOException) { }
    }
}

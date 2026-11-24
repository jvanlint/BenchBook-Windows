namespace BenchBook.Core.Model;

/// <summary>Every record in a library: the contents of library.json.</summary>
public sealed class LibrarySnapshot
{
    /// <summary>The data model version this app reads and writes (library-format.md, "Compatibility").</summary>
    public const string CurrentSchemaVersion = "6.0.0";

    public string SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<Project> Projects { get; set; } = [];
    public List<Reference> References { get; set; } = [];
    public List<Asset> Assets { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
    public List<ReferenceTag> ReferenceTags { get; set; } = [];
    public List<VideoMoment> VideoMoments { get; set; } = [];
    public List<Markup> Markups { get; set; } = [];
    public List<Paint> Paints { get; set; } = [];
    public List<OwnedPaint> OwnedPaints { get; set; } = [];
    public List<PaintMixPart> PaintMixParts { get; set; } = [];
    public List<ProjectPaint> ProjectPaints { get; set; } = [];
    public List<AftermarketItem> AftermarketItems { get; set; } = [];
    public List<BuildPhoto> BuildPhotos { get; set; } = [];
    public List<BuildPhotoTag> BuildPhotoTags { get; set; } = [];
    public List<Kit> Kits { get; set; } = [];

    internal IEnumerable<LibraryRecord> AllRecords() =>
        Projects.Cast<LibraryRecord>()
            .Concat(References).Concat(Assets).Concat(Tags).Concat(ReferenceTags)
            .Concat(VideoMoments).Concat(Markups).Concat(Paints).Concat(OwnedPaints).Concat(PaintMixParts)
            .Concat(ProjectPaints).Concat(AftermarketItems).Concat(BuildPhotos).Concat(BuildPhotoTags).Concat(Kits);

    internal void Tidy()
    {
        foreach (var record in AllRecords()) record.Tidy();
    }

    /// <summary>
    /// Problems that would stop the snapshot from being stored: ids that aren't
    /// UUIDs or appear twice, links to records that don't exist, required values
    /// that are empty, and duplicate tag names or paint keys. Empty when it's sound.
    /// A project photo that no longer exists isn't a problem: it shows none.
    /// </summary>
    public List<string> FindProblems()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(Ids.Comparer);
        foreach (var record in AllRecords())
        {
            if (!Ids.IsValid(record.Id)) problems.Add($"“{record.Id}” isn't a valid id.");
            else if (!seen.Add(record.Id)) problems.Add($"The id {record.Id} is used twice.");
        }

        HashSet<string> IdsOf(IEnumerable<LibraryRecord> records) => new(records.Select(r => r.Id), Ids.Comparer);
        var projects = IdsOf(Projects);
        var references = IdsOf(References);
        var assets = IdsOf(Assets);
        var tags = IdsOf(Tags);
        var paints = IdsOf(Paints);
        var buildPhotos = IdsOf(BuildPhotos);
        var mixes = new HashSet<string>(Paints.Where(p => p.IsMix).Select(p => p.Id), Ids.Comparer);

        void Link(string what, string id, string? target, HashSet<string> targets, bool optional = false)
        {
            if (target is null)
            {
                if (!optional) problems.Add($"The {what} {id} doesn't name the record it belongs to.");
            }
            else if (!targets.Contains(target))
            {
                problems.Add($"The {what} {id} refers to {target}, which isn't in the backup.");
            }
        }

        void Twice<T>(IEnumerable<T> links, Func<T, (string?, string?)> pair, string message)
        {
            foreach (var group in links.GroupBy(l => { var (a, b) = pair(l); return (Ids.Normalize(a), Ids.Normalize(b)); }).Where(g => g.Count() > 1))
                problems.Add(string.Format(message, group.Key.Item1, group.Key.Item2));
        }

        foreach (var p in Projects)
            if (string.IsNullOrWhiteSpace(p.Name)) problems.Add($"The project {p.Id} has no name.");

        foreach (var r in References)
        {
            Link("reference", r.Id, r.ProjectId, projects);
            Link("reference", r.Id, r.AssetId, assets, optional: true);
            if (ReferenceKind.HasAsset(r.Kind) && r.AssetId is null)
                problems.Add($"The {r.Kind} reference {r.Id} has no file.");
        }

        foreach (var a in Assets)
            if (!LibraryPaths.IsSafeOriginalPath(a.OriginalRelativePath))
                problems.Add($"The file “{a.OriginalRelativePath}” isn't in the Originals folder.");

        foreach (var t in ReferenceTags)
        {
            Link("tag link", t.Id, t.ReferenceId, references);
            Link("tag link", t.Id, t.TagId, tags);
        }
        Twice(ReferenceTags, t => (t.ReferenceId, t.TagId), "The reference {0} has the tag {1} twice.");

        foreach (var m in VideoMoments) Link("video moment", m.Id, m.ReferenceId, references);

        foreach (var m in Markups)
        {
            if ((m.ReferenceId is null) == (m.BuildPhotoId is null))
                problems.Add($"The markup {m.Id} must belong to one reference or one build photo.");
            else if (m.ReferenceId is not null) Link("markup", m.Id, m.ReferenceId, references);
            else Link("markup", m.Id, m.BuildPhotoId, buildPhotos);
        }

        foreach (var o in OwnedPaints) Link("owned paint", o.Id, o.PaintId, paints);
        foreach (var group in OwnedPaints.GroupBy(o => Ids.Normalize(o.PaintId)).Where(g => g.Count() > 1))
            problems.Add($"The paint {group.Key} is owned twice.");

        foreach (var part in PaintMixParts)
        {
            Link("recipe line", part.Id, part.MixId, mixes);
            Link("recipe line", part.Id, part.PaintId, paints);
            if (mixes.Contains(part.PaintId)) problems.Add($"The recipe line {part.Id} uses a mix, which a recipe can't.");
            if (!(part.Parts > 0)) problems.Add($"The recipe line {part.Id} has no parts.");
        }
        Twice(PaintMixParts, p => (p.MixId, p.PaintId), "The mix {0} has the paint {1} twice.");

        foreach (var pp in ProjectPaints)
        {
            Link("project paint", pp.Id, pp.ProjectId, projects);
            Link("project paint", pp.Id, pp.PaintId, paints, optional: true);
        }
        foreach (var item in AftermarketItems)
        {
            Link("aftermarket item", item.Id, item.ProjectId, projects);
            Link("aftermarket item", item.Id, item.ImageAssetId, assets, optional: true);
            if (string.IsNullOrWhiteSpace(item.ProductName)) problems.Add($"The aftermarket item {item.Id} has no name.");
        }

        foreach (var photo in BuildPhotos)
        {
            Link("build photo", photo.Id, photo.ProjectId, projects);
            Link("build photo", photo.Id, photo.AssetId, assets);
        }
        foreach (var t in BuildPhotoTags)
        {
            Link("build photo tag link", t.Id, t.BuildPhotoId, buildPhotos);
            Link("build photo tag link", t.Id, t.TagId, tags);
        }
        Twice(BuildPhotoTags, t => (t.BuildPhotoId, t.TagId), "The build photo {0} has the tag {1} twice.");

        foreach (var kit in Kits)
        {
            Link("kit", kit.Id, kit.PictureAssetId, assets, optional: true);
            Link("kit", kit.Id, kit.ProjectId, projects, optional: true);
            if (string.IsNullOrWhiteSpace(kit.Brand) || string.IsNullOrWhiteSpace(kit.Name))
                problems.Add($"The kit {kit.Id} has no brand or name.");
        }

        foreach (var paint in Paints)
            if (string.IsNullOrWhiteSpace(paint.Brand) || (!paint.IsMix && string.IsNullOrWhiteSpace(paint.Code)))
                problems.Add($"The paint {paint.Id} has no brand or code.");

        foreach (var group in Tags.GroupBy(t => t.NormalizedName, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"The tag name “{group.Key}” is used twice.");
        foreach (var group in Paints.GroupBy(p => p.IdentityKey, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"The paint “{group.Key}” is in the catalogue twice.");

        return problems;
    }
}

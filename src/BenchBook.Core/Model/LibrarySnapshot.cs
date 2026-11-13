namespace BenchBook.Core.Model;

/// <summary>Every record in a library: the contents of library.json.</summary>
public sealed class LibrarySnapshot
{
    /// <summary>The data model version this app reads and writes.</summary>
    public const string CurrentSchemaVersion = "1.0.0";

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
    public List<ProjectPaint> ProjectPaints { get; set; } = [];
    public List<AftermarketItem> AftermarketItems { get; set; } = [];

    internal IEnumerable<LibraryRecord> AllRecords() =>
        Projects.Cast<LibraryRecord>()
            .Concat(References).Concat(Assets).Concat(Tags).Concat(ReferenceTags)
            .Concat(VideoMoments).Concat(Markups).Concat(Paints).Concat(OwnedPaints)
            .Concat(ProjectPaints).Concat(AftermarketItems);

    internal void Tidy()
    {
        foreach (var record in AllRecords()) record.Tidy();
    }

    /// <summary>
    /// Problems that would stop the snapshot from being stored: ids that aren't
    /// UUIDs or appear twice, links to records that don't exist, required values
    /// that are empty, and duplicate tag names or paint keys. Empty when it's sound.
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
        foreach (var group in ReferenceTags.GroupBy(t => (Ids.Normalize(t.ReferenceId), Ids.Normalize(t.TagId))).Where(g => g.Count() > 1))
            problems.Add($"The reference {group.Key.Item1} has the tag {group.Key.Item2} twice.");

        foreach (var m in VideoMoments) Link("video moment", m.Id, m.ReferenceId, references);
        foreach (var m in Markups) Link("markup", m.Id, m.ReferenceId, references);
        foreach (var o in OwnedPaints) Link("owned paint", o.Id, o.PaintId, paints);
        foreach (var group in OwnedPaints.GroupBy(o => Ids.Normalize(o.PaintId)).Where(g => g.Count() > 1))
            problems.Add($"The paint {group.Key} is owned twice.");

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

        foreach (var paint in Paints)
            if (string.IsNullOrWhiteSpace(paint.Brand) || string.IsNullOrWhiteSpace(paint.Code))
                problems.Add($"The paint {paint.Id} has no brand or code.");

        foreach (var group in Tags.GroupBy(t => t.NormalizedName, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"The tag name “{group.Key}” is used twice.");
        foreach (var group in Paints.GroupBy(p => p.IdentityKey, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"The paint “{group.Key}” is in the catalogue twice.");

        return problems;
    }
}

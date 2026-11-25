namespace BenchBook.Core.Tests;

/// <summary>
/// The spec's sample files (..\BenchBook\spec\fixtures), copied into this
/// project so the tests also run where only this repo is checked out, such as
/// GitHub's release build. Where the spec is beside this repo, the copies are
/// checked against it, so they can't quietly drift apart.
/// </summary>
public class SpecFixtures
{
    public static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static readonly string Spec = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "BenchBook", "spec", "fixtures"));

    [Theory]
    [InlineData("kits-scalemates.csv")]
    [InlineData("paints-scalemates.csv")]
    public void TheCopiesMatchTheSpec(string name)
    {
        var original = Path.Combine(Spec, name);
        if (!File.Exists(original)) Assert.Skip("The spec isn't beside this repo, so the copy can't be checked.");
        Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(Path.Combine(Folder, name)));
    }
}

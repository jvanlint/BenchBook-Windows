using BenchBook.Core.Model;

namespace BenchBook.Core.Tests;

public class ValuesTests
{
    [Fact]
    public void IdsAreUppercaseAndCompareWithoutCase()
    {
        var id = Ids.New();
        Assert.Equal(id.ToUpperInvariant(), id);
        Assert.Equal(36, id.Length);
        Assert.Equal("0F8C4E2A-6B1D-4C55-9E3A-2D7B1F9A4C10", Ids.Normalize("0f8c4e2a-6b1d-4c55-9e3a-2d7b1f9a4c10"));
        Assert.True(Ids.Comparer.Equals("0f8c4e2a-6b1d-4c55-9e3a-2d7b1f9a4c10", "0F8C4E2A-6B1D-4C55-9E3A-2D7B1F9A4C10"));
        Assert.Null(Ids.Normalize("not-an-id"));
        Assert.Null(Ids.Normalize("{0F8C4E2A-6B1D-4C55-9E3A-2D7B1F9A4C10}"));
    }

    [Fact]
    public void DatesAreWrittenInUtcWithMilliseconds()
    {
        var date = new DateTimeOffset(2026, 11, 13, 19, 41, 7, 250, TimeSpan.FromHours(10));
        Assert.Equal("2026-11-13T09:41:07.250Z", IsoDate.Write(date));
        Assert.Equal("2026-11-13T09:41:07.000Z", IsoDate.Write(new DateTimeOffset(2026, 11, 13, 9, 41, 7, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("2026-11-13T09:41:07.250Z", "2026-11-13T09:41:07.250Z")]
    [InlineData("2026-11-13T09:41:07Z", "2026-11-13T09:41:07.000Z")]
    [InlineData("2026-11-13T19:41:07.250+10:00", "2026-11-13T09:41:07.250Z")]
    [InlineData("2026-11-13T09:41:07.2509Z", "2026-11-13T09:41:07.250Z")]
    public void DatesAreReadFromIso8601(string text, string expected)
    {
        Assert.True(IsoDate.TryRead(text, out var date));
        Assert.Equal(expected, IsoDate.Write(date));
    }

    [Theory]
    [InlineData("")]
    [InlineData("13/11/2026")]
    [InlineData("yesterday")]
    public void OtherTextIsNotADate(string text) => Assert.False(IsoDate.TryRead(text, out _));

    [Theory]
    [InlineData("Originals/A.jpg", true)]
    [InlineData("Posters/B.jpg", true)]
    [InlineData("/Originals/A.jpg", false)]
    [InlineData("C:/Originals/A.jpg", false)]
    [InlineData("C:A.jpg", false)]
    [InlineData("Originals/../A.jpg", false)]
    [InlineData("Originals/./A.jpg", false)]
    [InlineData("Originals\\A.jpg", false)]
    [InlineData("Originals//A.jpg", false)]
    [InlineData("Originals/CON", false)]
    [InlineData("Originals/nul.txt", false)]
    [InlineData("Originals/COM1.jpg", false)]
    [InlineData("Originals/A.jpg.", false)]
    [InlineData("Originals/A.jpg ", false)]
    [InlineData("Originals/A?.jpg", false)]
    [InlineData("Originals/A.jpg:stream", false)]
    [InlineData("", false)]
    public void RelativePathsAreChecked(string path, bool safe) => Assert.Equal(safe, LibraryPaths.IsSafeRelativePath(path));

    [Theory]
    [InlineData("Originals/A.jpg", true)]
    [InlineData("Originals/.partial-x", false)]
    [InlineData("Posters/A.jpg", false)]
    [InlineData("Originals/Sub/A.jpg", false)]
    public void AssetPathsAreDirectlyInOriginals(string path, bool safe) => Assert.Equal(safe, LibraryPaths.IsSafeOriginalPath(path));
}

public class NaturalOrderTests
{
    [Fact]
    public void DigitRunsCompareAsNumbersAndCaseIsIgnored()
    {
        string[] names = ["XF-10", "xf-2", "XF-1", "F-16CJ", "f-4", "Spitfire Mk IX", "spitfire mk ix", "A"];
        var sorted = names.Order(NaturalComparer.Instance).ToArray();
        Assert.Equal(["A", "f-4", "F-16CJ", "Spitfire Mk IX", "spitfire mk ix", "XF-1", "xf-2", "XF-10"], sorted);
    }

    [Fact]
    public void LeadingZerosDontChangeTheOrder()
    {
        Assert.True(NaturalComparer.Instance.Compare("XF-007", "XF-10") < 0);
        Assert.True(NaturalComparer.Instance.Compare("XF-2", "XF-02") != 0);
    }
}

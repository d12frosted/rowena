using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class AwayDigestTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static Notice At(int minutesAgo, NoticeKind kind, string text, int count = 0, long gil = 0) =>
        new(kind, T0 - TimeSpan.FromMinutes(minutesAgo), text, count, gil);

    [Fact]
    public void SalesFoldIntoOneLineWithTheTotal()
    {
        var lines = AwayDigest.Fold(
        [
            At(41, NoticeKind.Sale, "Sold while you were away: 2 things for 38,443 gil.", count: 2, gil: 38_443),
            At(41, NoticeKind.Sale, "Sold while you were away: 3 things for 76,000 gil.", count: 3, gil: 76_000),
        ]);

        var line = Assert.Single(lines);
        Assert.Equal(NoticeKind.Sale, line.Kind);
        Assert.Equal("Sold while you were away: 5 things for 114,443 gil.", line.Text);
    }

    [Fact]
    public void ASingleSaleKeepsItsOwnWording()
    {
        var lines = AwayDigest.Fold(
        [
            At(5, NoticeKind.Sale, "Sold while you were away: 2x Mythrite Ore for 3,788 gil.", count: 2, gil: 3_788),
        ]);

        Assert.Equal("Sold while you were away: 2x Mythrite Ore for 3,788 gil.", Assert.Single(lines).Text);
    }

    [Fact]
    public void TheFoldedSaleTakesTheNewestTime()
    {
        var lines = AwayDigest.Fold(
        [
            At(50, NoticeKind.Sale, "", count: 1, gil: 1),
            At(10, NoticeKind.Sale, "", count: 1, gil: 1),
        ]);

        Assert.Equal(T0 - TimeSpan.FromMinutes(10), Assert.Single(lines).At);
    }

    [Fact]
    public void EverythingElsePassesThroughNewestFirst()
    {
        var lines = AwayDigest.Fold(
        [
            At(37, NoticeKind.Undercut, "Thavnairian Horsetail: undercut."),
            At(17, NoticeKind.VendorFind, "Clear Demimateria III: vendor find."),
            At(41, NoticeKind.Briefing, "flips pay 13M."),
            At(20, NoticeKind.Sale, "sold", count: 1, gil: 5),
        ]);

        Assert.Equal(
            ["Clear Demimateria III: vendor find.", "sold", "Thavnairian Horsetail: undercut.", "flips pay 13M."],
            lines.Select(line => line.Text));
    }

    [Fact]
    public void NothingFoldsToNothing() => Assert.Empty(AwayDigest.Fold([]));
}

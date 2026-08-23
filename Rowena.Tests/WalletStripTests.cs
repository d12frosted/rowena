using Rowena.Core.Conversions;
using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class WalletStripTests
{
    private static readonly Resource Orange = new(ResourceKind.Currency, 41785, "Orange Gatherers' Scrip");
    private static readonly Resource Purple = new(ResourceKind.Currency, 33913, "Purple Crafters' Scrip");
    private static readonly Resource Seals = new(ResourceKind.Currency, 10307, "Centurio Seal");

    [Fact]
    public void APinnedCurrencyShowsEvenAtZero()
    {
        // "Is it worth going to earn scrips" is asked precisely when there are none.
        var rows = WalletStrip.Pick([new Holding(Orange, 0, 4_000)], pinned: [Orange.Id]);

        var row = Assert.Single(rows);
        Assert.Equal(Orange, row.Currency);
        Assert.True(row.Pinned);
        Assert.False(row.NearCap);
    }

    [Fact]
    public void APinnedCurrencyAlwaysShowsItsCap()
    {
        var rows = WalletStrip.Pick([new Holding(Orange, 12, 4_000)], pinned: [Orange.Id]);

        Assert.Equal(4_000, rows[0].Cap);
    }

    [Fact]
    public void AnUnpinnedCurrencyHalfwayToItsCapIsNoise()
    {
        // Seals at 78% are not a decision anyone makes in this window.
        var rows = WalletStrip.Pick([new Holding(Seals, 3_130, 4_000)], pinned: []);

        Assert.Empty(rows);
    }

    [Fact]
    public void AnUnpinnedCurrencyInItsLastTenthIsAWarning()
    {
        var rows = WalletStrip.Pick([new Holding(Seals, 3_600, 4_000)], pinned: []);

        var row = Assert.Single(rows);
        Assert.True(row.NearCap);
        Assert.False(row.Pinned);
    }

    [Fact]
    public void AnUnpinnedCurrencyWithoutACapNeverShows()
    {
        var rows = WalletStrip.Pick([new Holding(Seals, 1_000_000, null)], pinned: []);

        Assert.Empty(rows);
    }

    [Fact]
    public void APinnedCurrencyInItsLastTenthIsBothPinnedAndWarned()
    {
        var rows = WalletStrip.Pick([new Holding(Orange, 3_999, 4_000)], pinned: [Orange.Id]);

        Assert.True(rows[0].Pinned);
        Assert.True(rows[0].NearCap);
    }

    [Fact]
    public void PinnedComeFirstInPinnedOrderThenWarningsWorstFirst()
    {
        var other = new Resource(ResourceKind.Currency, 26533, "Allied Seal");

        var rows = WalletStrip.Pick(
            [
                new Holding(Seals, 3_700, 4_000),
                new Holding(Orange, 5, 4_000),
                new Holding(other, 19_900, 20_000),
                new Holding(Purple, 3_900, 4_000),
            ],
            pinned: [Orange.Id, Purple.Id]);

        Assert.Equal([Orange, Purple, other, Seals], rows.Select(row => row.Currency));
    }

    [Fact]
    public void PinnedOrderIsThePinnedListNotTheCatalogue()
    {
        // The catalogue lists currencies in whatever order it found them, which is no order
        // at all. The list in Settings is the one you arranged, so it wins.
        var rows = WalletStrip.Pick(
            [new Holding(Orange, 1, 4_000), new Holding(Purple, 1, 4_000)],
            pinned: [Purple.Id, Orange.Id]);

        Assert.Equal([Purple, Orange], rows.Select(row => row.Currency));
    }
}

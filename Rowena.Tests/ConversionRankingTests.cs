using Rowena.Core.Conversions;
using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class ConversionRankingTests
{
    private static readonly Resource Mat = Resource.Item(10, "Mat");
    private static readonly Resource Slow = Resource.Item(20, "Slow Seller");
    private static readonly Resource Brisk = Resource.Item(30, "Brisk Seller");

    private static Conversion Craft(string id, Resource product) =>
        new(id, id, [new ResourceAmount(Mat, 1)], [new ResourceAmount(product, 1)], "craft");

    /// <summary>Mats are free here so the margin is entirely the product's price.</summary>
    private static Func<uint, OrderBook?> Books(double slowVelocity, double briskVelocity) => id =>
        id switch
        {
            10 => OrderBook.Create(10, [new Listing(1, 1_000, "w")]),
            20 => OrderBook.Create(20, [new Listing(2_000_000, 50, "w")], slowVelocity),
            30 => OrderBook.Create(30, [new Listing(50_000, 500, "w")], briskVelocity),
            _ => null,
        };

    [Fact]
    public void VelocityBeatsMargin()
    {
        // The whole reason this exists. A two million gil margin that clears once a fortnight
        // is worth less per day than a fifty thousand one that clears twenty times.
        var ranked = ConversionRanking.ByGilPerDay(
            [Craft("slow", Slow), Craft("brisk", Brisk)],
            Books(slowVelocity: 1d / 14d, briskVelocity: 20d),
            MarketTax.Standard);

        Assert.Equal("brisk", ranked[0].Conversion.Id);
        Assert.True(ranked[0].GilPerDay > ranked[1].GilPerDay);

        // And the margin ordering really is the other way round, so this is an inversion and
        // not a coincidence of the numbers picked.
        Assert.True(ranked[1].Quote.Profit > ranked[0].Quote.Profit);
    }

    [Fact]
    public void SomethingThatNeverSellsEarnsNothing()
    {
        var ranked = ConversionRanking.ByGilPerDay(
            [Craft("slow", Slow)],
            Books(slowVelocity: 0d, briskVelocity: 0d),
            MarketTax.Standard);

        Assert.Equal(0d, ranked[0].RunsPerDay);
        Assert.Equal(0, ranked[0].GilPerDay);

        // The margin is still real and still reported. It is the daily figure that is zero.
        Assert.True(ranked[0].Quote.Profit > 0);
    }

    [Fact]
    public void YourOwnThroughputCanBeTheTighterLimit()
    {
        var market = Books(slowVelocity: 0d, briskVelocity: 20d);

        var uncapped = ConversionRanking.ByGilPerDay([Craft("brisk", Brisk)], market, MarketTax.Standard)[0];
        var capped = ConversionRanking.ByGilPerDay([Craft("brisk", Brisk)], market, MarketTax.Standard, 3d)[0];

        Assert.Equal(20d, uncapped.RunsPerDay);
        Assert.Equal(3d, capped.RunsPerDay);
        Assert.True(capped.GilPerDay < uncapped.GilPerDay);
    }

    [Fact]
    public void TheMarketCanStillBeTheTighterLimit()
    {
        // A generous cap must not invent demand that is not there.
        var earnings = ConversionRanking.ByGilPerDay(
            [Craft("brisk", Brisk)],
            Books(0d, briskVelocity: 2d),
            MarketTax.Standard,
            maxRunsPerDay: 100d)[0];

        Assert.Equal(2d, earnings.RunsPerDay);
    }

    [Fact]
    public void LossesStaySignedSoTheWorstSortsLast()
    {
        // Mats dearer than the product. Losing money quickly is worse than losing it slowly,
        // and clamping to zero would make those two look identical.
        Func<uint, OrderBook?> dear = id => id switch
        {
            10 => OrderBook.Create(10, [new Listing(90_000, 1_000, "w")]),
            30 => OrderBook.Create(30, [new Listing(50_000, 500, "w")], 20d),
            _ => null,
        };

        var earnings = ConversionRanking.ByGilPerDay([Craft("losing", Brisk)], dear, MarketTax.Standard)[0];

        Assert.True(earnings.Quote.Profit < 0);
        Assert.True(earnings.GilPerDay < 0);
    }

    [Fact]
    public void AnUnpriceableTradeEarnsNothingAndSaysWhy()
    {
        var earnings = ConversionRanking.ByGilPerDay(
            [Craft("unknown", Resource.Item(999, "Never Listed"))],
            Books(0d, 0d),
            MarketTax.Standard)[0];

        Assert.Equal(0, earnings.GilPerDay);
        Assert.False(earnings.Quote.IsExecutable);
        Assert.NotEmpty(earnings.Quote.Unpriced);
    }
}

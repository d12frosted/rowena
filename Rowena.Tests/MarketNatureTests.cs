using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class MarketNatureTests
{
    /// <summary>Sales that vary by a few percent, which is most healthy markets.</summary>
    private static long[] Calm(long around) =>
        [around, (long)(around * 1.02), (long)(around * 0.98), around, (long)(around * 1.01)];

    [Fact]
    public void StockThatWouldTakeMonthsToClearIsAGlut()
    {
        // Two thousand listed against ten a day is half a year of supply. Nothing about the
        // margin matters: you are behind two hundred days of other people's stock.
        var nature = MarketNature.Of(listed: 2000, salesPerDay: 10, Calm(1000));

        Assert.Equal(MarketCharacter.Glutted, nature.Character);
        Assert.Equal(200d, nature.DaysOfSupply!.Value, 3);
    }

    [Fact]
    public void SellingFasterThanItIsStockedIsHot()
    {
        var nature = MarketNature.Of(listed: 20, salesPerDay: 40, Calm(1000));

        Assert.Equal(MarketCharacter.Hot, nature.Character);
    }

    [Fact]
    public void ThinAndSlowWithFewSellersIsANiche()
    {
        // One a day and six listed. Nobody mass produces this, which is exactly why there is
        // room in it for somebody who turns up.
        var nature = MarketNature.Of(listed: 6, salesPerDay: 1, Calm(50_000));

        Assert.Equal(MarketCharacter.Niche, nature.Character);
    }

    [Fact]
    public void APriceThatJumpsAboutIsSwingy()
    {
        // The margin on a board like this is a number with a wide error bar, and a ranking that
        // treats it as exact is quietly overconfident.
        var nature = MarketNature.Of(listed: 40, salesPerDay: 8, [400, 1200, 500, 1600, 900, 1500]);

        Assert.Equal(MarketCharacter.Swingy, nature.Character);
    }

    [Fact]
    public void AnOrdinaryWorkingMarketIsSteady()
    {
        var nature = MarketNature.Of(listed: 40, salesPerDay: 8, Calm(1000));

        Assert.Equal(MarketCharacter.Steady, nature.Character);
        Assert.Equal(5d, nature.DaysOfSupply!.Value, 3);
    }

    [Fact]
    public void AGlutOutranksEverythingElseAboutIt()
    {
        // Wild prices and no sellers do not rescue a board with a year of stock on it.
        var nature = MarketNature.Of(listed: 5000, salesPerDay: 5, [100, 900, 200, 1500]);

        Assert.Equal(MarketCharacter.Glutted, nature.Character);
    }

    [Fact]
    public void SellingNothingAtAllIsNotANiche()
    {
        // A niche is slow. A market with no sales is not slow, it is shut, and calling it a
        // niche would dress up the worst rows in the table as opportunities.
        var nature = MarketNature.Of(listed: 3, salesPerDay: 0, []);

        Assert.Equal(MarketCharacter.Dead, nature.Character);
        Assert.Null(nature.DaysOfSupply);
    }

    [Fact]
    public void AThinMarketWithWildPricesIsAWarningRatherThanAnInvitation()
    {
        // Both are true of it, and the labels pull opposite ways: thin and quiet reads as an
        // opening, wildly priced reads as a caution. Measured, a module listing at nine million
        // against sales all over the place came out as a niche worth eight hundred thousand a
        // day, which is the wrong thing to say about it.
        var nature = MarketNature.Of(listed: 8, salesPerDay: 1, [200_000, 900_000, 300_000, 8_000_000]);

        Assert.Equal(MarketCharacter.Swingy, nature.Character);
    }

    [Fact]
    public void SlowWithACrowdOfSellersIsNotANicheEither()
    {
        var nature = MarketNature.Of(listed: 300, salesPerDay: 1.5, Calm(50_000));

        Assert.NotEqual(MarketCharacter.Niche, nature.Character);
    }

    [Fact]
    public void NoSaleHistoryLeavesNoOpinionOnTheSpread()
    {
        var nature = MarketNature.Of(listed: 40, salesPerDay: 8, []);

        Assert.Null(nature.Spread);
        Assert.Equal(MarketCharacter.Steady, nature.Character);
    }

    [Fact]
    public void TheSpreadIsMeasuredAgainstTheMiddleRatherThanTheExtremes()
    {
        // One silly sale in a run of ordinary ones should not make a market look wild, which is
        // what a plain standard deviation would do.
        var nature = MarketNature.Of(listed: 40, salesPerDay: 8, [1000, 1010, 990, 1005, 995, 999_999]);

        Assert.NotEqual(MarketCharacter.Swingy, nature.Character);
    }
}

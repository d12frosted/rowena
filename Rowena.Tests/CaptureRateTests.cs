using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class CaptureRateTests
{
    private const int Enough = 20;

    [Fact]
    public void TakingEverySaleIsAWholeMarket()
    {
        // Seventy sold over seven days on a board that moves ten a day is all of it.
        var rate = CaptureRate.Of([new Captured(1, 70, 10, 7)], Enough);

        Assert.Equal(1d, rate!.Value, 3);
    }

    [Fact]
    public void TakingAFifthOfWhatMovesIsAFifth()
    {
        var rate = CaptureRate.Of([new Captured(1, 70, 50, 7)], Enough);

        Assert.Equal(0.2d, rate!.Value, 3);
    }

    [Fact]
    public void SeveralItemsAreWeightedByHowMuchTheirMarketsMove()
    {
        // A busy market I barely touch should not be averaged flat against a quiet one I own:
        // what is wanted is the share of everything that moved, not the mean of two fractions.
        var rate = CaptureRate.Of(
            [new Captured(1, 70, 10, 7), new Captured(2, 7, 100, 7)],
            Enough);

        Assert.Equal(77d / 770d, rate!.Value, 4);
    }

    [Fact]
    public void TooFewSalesIsNoMeasurement()
    {
        // A share worked out from three sales is not a measurement, and offering it as one
        // would be worse than the honest ceiling it replaces.
        Assert.Null(CaptureRate.Of([new Captured(1, 3, 10, 7)], Enough));
    }

    [Fact]
    public void AMarketThatMovesNothingCannotBeAShareOfAnything()
    {
        // Selling into a board with no reported sales says nothing about capture, and dividing
        // by it would say everything.
        Assert.Null(CaptureRate.Of([new Captured(1, 70, 0, 7)], Enough));
    }

    [Fact]
    public void NobodyTakesMoreThanAWholeMarket()
    {
        // Sale rates lag and my own sales may not be in them yet, so the arithmetic can exceed
        // one. A capture above the whole market is a measurement fault, not a triumph.
        var rate = CaptureRate.Of([new Captured(1, 500, 10, 7)], Enough);

        Assert.Equal(1d, rate!.Value, 3);
    }

    [Fact]
    public void ItWillNotAnswerOffAFractionOfWhatISold()
    {
        // The items whose rate is missing are not a random sample. Whatever is left skews to the
        // small quiet markets a person takes most of, so measured off a third of the sales this
        // came out at eleven percent where the full set said four.
        var rate = CaptureRate.Of(
            [new Captured(1, 30, 10, 7), new Captured(2, 200, 0, 7)],
            Enough);

        Assert.Null(rate);
    }

    [Fact]
    public void AFewMissingRatesDoNotStopIt()
    {
        var rate = CaptureRate.Of(
            [new Captured(1, 90, 10, 7), new Captured(2, 5, 0, 7)],
            Enough);

        Assert.NotNull(rate);
    }

    [Fact]
    public void NoDaysIsNoWindowToMeasureOver() =>
        Assert.Null(CaptureRate.Of([new Captured(1, 70, 10, 0)], Enough));

    [Fact]
    public void NothingSoldIsNoMeasurement() => Assert.Null(CaptureRate.Of([], Enough));
}

using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class UndercutTests
{
    private static OrderBook Book(params (long Price, int Units)[] listings) =>
        OrderBook.Create(1, listings.Select(listing => new Listing(listing.Price, listing.Units, "Light")));

    [Fact]
    public void NothingBelowMeMeansNothingToDo()
    {
        Assert.Null(Undercut.Of(100, Book((100, 3), (120, 5)), 5));
    }

    [Fact]
    public void NoBookMeansNoAnswer()
    {
        Assert.Null(Undercut.Of(100, null, 5));
    }

    [Fact]
    public void TheTargetSitsTheMarginUnderTheCheapestBelowMe()
    {
        var plan = Undercut.Of(100, Book((90, 2), (95, 1), (100, 4)), 5);

        Assert.NotNull(plan);
        Assert.Equal(85, plan.Value.Target);
        Assert.Equal(90, plan.Value.Below);
        Assert.Equal(3, plan.Value.UnitsAhead);
    }

    [Fact]
    public void ATieAtMyPriceIsNotAhead()
    {
        // The board does not say which listing at my price is mine, so a tie cannot be
        // counted as someone in front of me without counting my own stock.
        Assert.Null(Undercut.Of(100, Book((100, 7)), 5));
    }

    [Fact]
    public void ThePriceNeverGoesBelowOneGil()
    {
        var plan = Undercut.Of(10, Book((3, 1)), 5);

        Assert.Equal(1, plan!.Value.Target);
    }

    [Fact]
    public void AZeroMarginMatchesTheFloor()
    {
        var plan = Undercut.Of(100, Book((90, 1)), 0);

        Assert.Equal(90, plan!.Value.Target);
    }
}

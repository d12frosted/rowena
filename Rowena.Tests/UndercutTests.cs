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

public class UndercutNobodyPaysTests
{
    private static OrderBook Book(long[] sales, params (long Price, int Units)[] listings) =>
        OrderBook.Create(1, listings.Select(listing => new Listing(listing.Price, listing.Units, "Light")), recentSales: sales);

    private static readonly long[] Paid = [1_200, 1_300, 1_450, 1_450, 1_500, 1_490, 1_400];

    [Fact]
    public void CheapestOnTheBoardIsStillRepricedWhenNobodyPaysIt()
    {
        // Measured: Mozzarella at 389,994 on a board where every listing sits there and
        // everything that trades goes for under 1,500. Nothing is below me and the old answer
        // was a dash, which reads as "fine".
        var plan = Undercut.Of(389_994, Book(Paid, (389_994, 1), (389_994, 3)), 5);

        Assert.NotNull(plan);
        Assert.Equal(UndercutWhy.NobodyPays, plan.Value.Why);
        Assert.Equal(1_450, plan.Value.Below);
        Assert.Equal(1_445, plan.Value.Target);
        Assert.Equal(0, plan.Value.UnitsAhead);
    }

    [Fact]
    public void AListingBelowMeThatIsStillAboveWhatPeoplePayIsNotTheTarget()
    {
        // A wall of two. Jumping the other one lands me at 299,995, which nobody pays either.
        var plan = Undercut.Of(389_994, Book(Paid, (300_000, 2), (389_994, 1)), 5);

        Assert.Equal(UndercutWhy.NobodyPays, plan!.Value.Why);
        Assert.Equal(1_445, plan.Value.Target);
        Assert.Equal(2, plan.Value.UnitsAhead);
    }

    [Fact]
    public void AListingBelowWhatPeoplePayIsUndercutInstead()
    {
        var plan = Undercut.Of(389_994, Book(Paid, (1_000, 2), (389_994, 1)), 5);

        Assert.Equal(UndercutWhy.Queue, plan!.Value.Why);
        Assert.Equal(995, plan.Value.Target);
    }

    [Fact]
    public void AFairPriceIsLeftAlone()
    {
        // Within the usual, and cheapest. Nothing to do.
        Assert.Null(Undercut.Of(1_600, Book(Paid, (1_600, 5)), 5));
    }

    [Fact]
    public void AFewSalesAreNotEnoughToCallAPriceUnrealistic()
    {
        // Two fire-sale buys should not drag a dear item down to them.
        Assert.Null(Undercut.Of(100_000, Book([1_000, 1_200], (100_000, 1)), 5));
    }
}

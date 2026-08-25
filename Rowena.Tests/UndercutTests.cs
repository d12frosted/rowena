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
    public void ThePlanCarriesWhatIAmAskingSoTheMoveCanBeSized()
    {
        // "2,000 -> 1,995" beside an ask of 4,000 reads as losing five gil. The move being
        // made is 4,000 to 1,995, and the plan has to carry both ends of it to say so.
        var plan = Undercut.Of(4_000, Book((2_000, 2), (4_000, 3)), 5);

        Assert.NotNull(plan);
        Assert.Equal(4_000, plan.Value.Mine);
        Assert.Equal(1_995, plan.Value.Target);
        Assert.Equal(-2_005, plan.Value.Move);
        Assert.Equal(-0.50125d, plan.Value.Share, 5);
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

public class UndercutRoomAboveTests
{
    private static OrderBook Book(long[] sales, params (long Price, int Units)[] listings) =>
        OrderBook.Create(1, listings.Select(listing => new Listing(listing.Price, listing.Units, "Light")), recentSales: sales);

    [Fact]
    public void ARaiseMovesTheOtherWay()
    {
        var plan = Undercut.Of(100, Book([190, 195, 200], (100, 1), (200, 3)), 5);

        Assert.NotNull(plan);
        Assert.Equal(UndercutWhy.RoomAbove, plan.Value.Why);
        Assert.Equal(95, plan.Value.Move);
        Assert.Equal(0.95d, plan.Value.Share, 5);
    }

    [Fact]
    public void CheapestWithRealRoomAboveIsRaised()
    {
        // Nothing under me, the next listing at double my ask, and sales saying people pay
        // up there. Sitting at 100 is money left behind, not a position.
        var plan = Undercut.Of(100, Book([190, 195, 200], (100, 1), (200, 3)), 5);

        Assert.NotNull(plan);
        Assert.Equal(UndercutWhy.RoomAbove, plan.Value.Why);
        Assert.Equal(200, plan.Value.Below);
        Assert.Equal(195, plan.Value.Target);
        Assert.Equal(0, plan.Value.UnitsAhead);
    }

    [Fact]
    public void ANarrowGapIsNotWorthTheBother()
    {
        // Eight gil of room is churn, not money. The bar is the diagnosis's own.
        Assert.Null(Undercut.Of(100, Book([100, 105, 108], (100, 1), (108, 3)), 5));
    }

    [Fact]
    public void RoomNobodyPaysUpInIsSomebodyElsesFantasy()
    {
        // The next listing at 100,000 over sales around 120. The gap is not on the table,
        // it is somebody parking an item.
        Assert.Null(Undercut.Of(100, Book([110, 120, 130], (100, 1), (100_000, 3)), 5));
    }

    [Fact]
    public void NoSalesMeansNoRaise()
    {
        // Without a sale on record, nothing says anybody pays what the next listing asks.
        Assert.Null(Undercut.Of(100, Book([], (100, 1), (200, 3)), 5));
    }

    [Fact]
    public void ATieAtMyPriceDoesNotHideTheRoom()
    {
        // Listings at my price are indistinguishable from mine, and my own stacks tying each
        // other is the common case. The ceiling is the next listing strictly above.
        var plan = Undercut.Of(100, Book([190, 195, 200], (100, 4), (200, 3)), 5);

        Assert.Equal(UndercutWhy.RoomAbove, plan!.Value.Why);
        Assert.Equal(195, plan.Value.Target);
    }

    [Fact]
    public void NobodyPayingWinsOverTheRoomAbove()
    {
        // Cheapest with room above, but my own ask is already past what people pay. The move
        // is down to where things sell, not further up the wall.
        var plan = Undercut.Of(1_000, Book([400, 400, 410, 420, 430], (1_000, 1), (5_000, 2)), 5);

        Assert.Equal(UndercutWhy.NobodyPays, plan!.Value.Why);
        Assert.Equal(405, plan.Value.Target);
    }

    [Fact]
    public void TheCeilingIsTheNextListingOfAnyQuality()
    {
        // My HQ is cheapest outright. Raising past the NQ above me would hand every buyer
        // who does not care about quality a cheaper listing than mine, so it is the ceiling.
        var book = OrderBook.Create(
            1,
            [new Listing(100, 1, "Light", IsHq: true), new Listing(200, 5, "Light"), new Listing(500, 1, "Light", IsHq: true)],
            recentSales: [190, 195, 200]);

        var plan = Undercut.Of(100, book, 5, hq: true);

        Assert.Equal(UndercutWhy.RoomAbove, plan!.Value.Why);
        Assert.Equal(200, plan.Value.Below);
        Assert.Equal(195, plan.Value.Target);
    }

    [Fact]
    public void AMarginThatEatsTheRoomMeansNoMove()
    {
        // The room is real by the bar, but the margin lands the target under where I already
        // am, and a raise that lowers the price is not a raise.
        Assert.Null(Undercut.Of(100, Book([110, 112, 114], (100, 1), (115, 3)), 20));
    }
}

public class UndercutQualityTests
{
    private static OrderBook Book(params Listing[] listings) => OrderBook.Create(1, listings);

    [Fact]
    public void AnHqListingIsNotUndercutByCheaperNq()
    {
        // Measured: an HQ nugget at 4,989 over NQ ones at 515. Nobody buying HQ takes the NQ
        // instead, so they are not in front, and 510 is money gone.
        Assert.Null(Undercut.Of(4_989, Book(new Listing(515, 99, "Light", IsHq: false)), 5, hq: true));
    }

    [Fact]
    public void AnHqListingIsUndercutByCheaperHq()
    {
        var plan = Undercut.Of(4_989, Book(new Listing(515, 99, "Light"), new Listing(4_000, 1, "Light", IsHq: true)), 5, hq: true);

        Assert.Equal(3_995, plan!.Value.Target);
        Assert.Equal(1, plan.Value.UnitsAhead);
    }

    [Fact]
    public void AnNqListingIsUndercutByCheaperHq()
    {
        // Somebody wanting NQ takes a cheaper HQ happily, so it is in front of mine.
        var plan = Undercut.Of(600, Book(new Listing(550, 2, "Light", IsHq: true)), 5, hq: false);

        Assert.Equal(545, plan!.Value.Target);
    }
}

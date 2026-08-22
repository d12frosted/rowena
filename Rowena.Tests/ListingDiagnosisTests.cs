using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class ListingDiagnosisTests
{
    private static OrderBook Book(
        (long Price, int Quantity)[] listings,
        double velocity = 10,
        long[]? sales = null) =>
        OrderBook.Create(
            1,
            listings.Select(l => new Listing(l.Price, l.Quantity, "")),
            velocity,
            recentSales: sales ?? []);

    private static ListingDiagnosis? Diagnose(
        long mine,
        int units,
        OrderBook book,
        long vendor = 0,
        int horizonDays = 7) =>
        ListingDiagnosis.Of(mine, units, book, vendor, MarketTax.None, horizonDays);

    [Fact]
    public void BeingTheCheapestIsNothingToDo()
    {
        var call = Diagnose(1000, 5, Book([(1000, 5), (1200, 10)]));

        Assert.Equal(ListingCall.Hold, call!.Value.Call);
        Assert.Equal(0, call.Value.UnitsAhead);
    }

    [Fact]
    public void AThinQueueAheadIsWorthWaitingOut()
    {
        // Three units ahead on a board that sells ten a day: they are gone this afternoon,
        // and undercutting to jump three units is a haircut for nothing. This is the whole
        // reason the plugin reads depth rather than the floor.
        var call = Diagnose(1000, 5, Book([(900, 3), (1000, 5)], velocity: 10));

        Assert.Equal(ListingCall.Wait, call!.Value.Call);
        Assert.Equal(3, call.Value.UnitsAhead);
        Assert.Equal(0.8d, call.Value.DaysToClear!.Value, 3);
    }

    [Fact]
    public void ALongQueueAheadIsWorthChasing()
    {
        var call = Diagnose(1000, 5, Book([(900, 200), (1000, 5)], velocity: 10));

        Assert.Equal(ListingCall.Chase, call!.Value.Call);
        Assert.Equal(200, call.Value.UnitsAhead);
    }

    [Fact]
    public void TheQueueCountsWhatIAmStillHoldingToo()
    {
        // Twenty of mine behind five of theirs, at ten a day, is two and a half days before
        // the last of mine goes. Counting only the queue ahead would call that done sooner
        // than it is.
        var call = Diagnose(1000, 20, Book([(900, 5), (1000, 20)], velocity: 10));

        Assert.Equal(2.5d, call!.Value.DaysToClear!.Value, 3);
    }

    [Fact]
    public void AVendorPayingMoreThanMyOwnAskingPriceSettlesIt()
    {
        var call = Diagnose(1000, 5, Book([(1000, 5)]), vendor: 1200);

        Assert.Equal(ListingCall.Vendor, call!.Value.Call);
        Assert.Equal(1200, call.Value.VendorNet);
    }

    [Fact]
    public void TheVendorIsJudgedAgainstWhatIWouldActuallyKeep()
    {
        // Nine hundred net of a five percent cut is 855, so a vendor paying 900 wins even
        // though the sticker price looks the same.
        var call = ListingDiagnosis.Of(900, 5, Book([(900, 5)]), 900, MarketTax.Standard, 7);

        Assert.Equal(ListingCall.Vendor, call!.Value.Call);
        Assert.Equal(855, call.Value.NetHolding);
    }

    [Fact]
    public void APriceNothingHasSoldNearIsCalledOut()
    {
        // Listed at four thousand where the last dozen sales were around a thousand: being
        // the cheapest on the board says nothing when nobody is buying at that level.
        var call = Diagnose(4000, 5, Book([(4000, 5)], sales: [1000, 1100, 900, 1000]));

        Assert.Equal(ListingCall.Overpriced, call!.Value.Call);
    }

    [Fact]
    public void APriceRecentSalesSupportIsNotCalledOut()
    {
        var call = Diagnose(1100, 5, Book([(1100, 5)], sales: [1000, 1100, 900, 1000]));

        Assert.Equal(ListingCall.Hold, call!.Value.Call);
    }

    [Fact]
    public void BeingTheCheapestWellUnderWhatPeoplePayIsMoneyLeftBehind()
    {
        // Measured: a nugget asking 895 while the last forty sales went at 1,199. Nothing is
        // wrong with it, it will sell within the hour, and that is the problem.
        var call = Diagnose(895, 5, Book([(895, 5), (1300, 20)], sales: [1199, 1200, 1150]));

        Assert.Equal(ListingCall.Underpriced, call!.Value.Call);
        Assert.Equal(1199, call.Value.TypicalSale);
    }

    [Fact]
    public void BeingCheapestByAHairIsNotHeadroom()
    {
        // Measured: a nugget at 895 with the next listing at 900, while recent sales say 1,196.
        // That median is what the board looked like before it filled in underneath. Raising to
        // 899 earns four gil, and raising past 900 gives up the front of the queue, so the
        // distance from past sales is the wrong thing to read here.
        var call = Diagnose(895, 5, Book([(895, 5), (900, 2), (998, 3)], sales: [1196, 1200, 1150]));

        Assert.Equal(ListingCall.Hold, call!.Value.Call);
        Assert.Equal(899, call.Value.CouldAsk);
    }

    [Fact]
    public void HeadroomStopsShortOfTheNextListing()
    {
        var call = Diagnose(895, 5, Book([(895, 5), (1300, 20)], sales: [1199, 1200, 1150]));

        Assert.Equal(1299, call!.Value.CouldAsk);
    }

    [Fact]
    public void RoomToRaiseNobodyWouldPayIsNotWorthTaking()
    {
        // A gap above me is only headroom if people are buying up there. These sales say they
        // are not, so the gap is somebody else's fantasy listing rather than my opportunity.
        var call = Diagnose(895, 5, Book([(895, 5), (9000, 20)], sales: [900, 890, 910]));

        Assert.Equal(ListingCall.Hold, call!.Value.Call);
    }

    [Fact]
    public void AloneOnTheBoardIsNoHeadroomEither()
    {
        var call = Diagnose(1000, 5, Book([(1000, 5)], sales: [2000, 2100]));

        Assert.Null(call!.Value.CouldAsk);
        Assert.Equal(ListingCall.Hold, call.Value.Call);
    }

    [Fact]
    public void BeingSlightlyUnderTheUsualPriceIsJustCompeting()
    {
        var call = Diagnose(1100, 5, Book([(1100, 5)], sales: [1199, 1200, 1150, 1250]));

        Assert.Equal(ListingCall.Hold, call!.Value.Call);
    }

    [Fact]
    public void UnderTheUsualPriceBehindAQueueIsNotMoneyLeftBehind()
    {
        // Cheap and still not first in line is an ordinary market, not an opportunity: raising
        // the price here only lengthens the queue ahead of you.
        var call = Diagnose(895, 5, Book([(500, 200), (895, 5)], sales: [1199, 1200, 1150]));

        Assert.NotEqual(ListingCall.Underpriced, call!.Value.Call);
    }

    [Fact]
    public void WhatItActuallySellsForIsKeptForEveryReading()
    {
        var call = Diagnose(1000, 5, Book([(1000, 5)], sales: [900, 1000, 1100]));

        Assert.Equal(1000, call!.Value.TypicalSale);
    }

    [Fact]
    public void NoHistoryMeansNoOpinionOnWhatItSellsFor()
    {
        var call = Diagnose(1000, 5, Book([(1000, 5)]));

        Assert.Null(call!.Value.TypicalSale);
    }

    [Fact]
    public void NothingSellingAtAllIsItsOwnAnswer()
    {
        var call = Diagnose(1000, 5, Book([(900, 3), (1000, 5)], velocity: 0));

        Assert.Equal(ListingCall.Stuck, call!.Value.Call);
        Assert.Null(call.Value.DaysToClear);
    }

    [Fact]
    public void ChasingIsPricedSoTheHaircutIsVisible()
    {
        // The point is not that chasing is right, it is what chasing costs: this is a third
        // off, and that is the number worth seeing before dropping the price.
        var call = ListingDiagnosis.Of(1500, 5, Book([(1000, 200), (1500, 5)]), 0, MarketTax.Standard, 7);

        Assert.Equal(1425, call!.Value.NetHolding);
        Assert.Equal(950, call.Value.NetChasing);
        Assert.Equal(1000, call.Value.Floor);
    }

    [Fact]
    public void AnEmptyBoardLeavesTheAnswerOpen()
    {
        // Nothing listed but mine is not a diagnosis, and the vendor still buys it.
        var call = Diagnose(1000, 5, OrderBook.Empty(1));

        Assert.Equal(ListingCall.Stuck, call!.Value.Call);
    }

    [Fact]
    public void NoBookIsNoAnswerRatherThanABadOne() =>
        Assert.Null(ListingDiagnosis.Of(1000, 5, null, 0, MarketTax.None, 7));
}

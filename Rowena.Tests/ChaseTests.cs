using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class ChaseTests
{
    private const int Patience = 7;

    private static OrderBook Book(
        double perDay,
        IReadOnlyList<long> sales,
        params (long Price, int Units)[] listings) =>
        OrderBook.Create(
            1,
            listings.Select(listing => new Listing(listing.Price, listing.Units, "Light")),
            perDay,
            recentSales: sales);

    private static ChaseVerdict Read(long mine, OrderBook book, int patience = Patience) =>
        Read(mine, book, MarketTax.None, patience);

    private static ChaseVerdict Read(long mine, OrderBook book, MarketTax tax, int patience = Patience)
    {
        var plan = Undercut.Of(mine, book, 5);
        Assert.NotNull(plan);

        return Chase.Of(plan.Value, book, tax, patience);
    }

    /// <summary>Enough sales for the median to be worth acting on, all at the same price.</summary>
    private static long[] Sold(long each, int times = 9) => [.. Enumerable.Repeat(each, times)];

    [Fact]
    public void AnOrdinaryUndercutIsNoDecisionAtAll()
    {
        // Seven gil off a hundred is competition, not a change of price.
        var verdict = Read(100, Book(10d, Sold(100), (98, 2), (100, 5)));

        Assert.Equal(ChaseCall.Follow, verdict.Call);
    }

    [Fact]
    public void TheCutIsReportedEvenWhenThereIsNothingToArgueAbout()
    {
        var verdict = Read(100, Book(10d, Sold(100), (98, 2), (100, 5)));

        Assert.Equal(7, verdict.Cut);
        Assert.Equal(0.07d, verdict.Share, 5);
    }

    [Fact]
    public void ARaiseIsNotAChase()
    {
        // Nothing is under me and the next listing sits well above. That is the other move.
        var verdict = Read(100, Book(10d, Sold(190), (100, 1), (200, 3)));

        Assert.Equal(ChaseCall.Follow, verdict.Call);
    }

    [Fact]
    public void PricingDownToWhatPeopleActuallyPayIsNotAChase()
    {
        // Asking 389,994 where things trade at 1,200 is my own mistake, not somebody else's
        // dump, however steep the correction looks.
        var verdict = Read(389_994, Book(10d, Sold(1_200), (300_000, 2), (389_994, 1)));

        Assert.Equal(ChaseCall.Follow, verdict.Call);
    }

    [Fact]
    public void CheapStockUnderWhatTheThingIsWorthIsWorthBuyingRatherThanJoining()
    {
        // Two units at 2,000 against sales at 4,000. Taking them costs 4,000 and they come
        // back as 8,000, so the answer is to buy somebody else's panic, not to match it.
        var verdict = Read(4_000, Book(10d, Sold(4_000), (2_000, 2), (4_000, 5)));

        Assert.Equal(ChaseCall.BuyOut, verdict.Call);
        Assert.Equal(2, verdict.UnitsUnder);
        Assert.Equal(4_000, verdict.BuyOutCost);
        Assert.Equal(8_000, verdict.BuyOutBack);
        Assert.Equal(2_005, verdict.Cut);
    }

    [Fact]
    public void BuyingOutCountsTheBoardsCutOnTheWayInAndOnTheWayBack()
    {
        // 4,000 of stock plus the buyer's 5%, against 8,000 back less the seller's 5%.
        var verdict = Read(4_000, Book(10d, Sold(4_000), (2_000, 2), (4_000, 5)), MarketTax.Standard);

        Assert.Equal(ChaseCall.BuyOut, verdict.Call);
        Assert.Equal(4_200, verdict.BuyOutCost);
        Assert.Equal(7_600, verdict.BuyOutBack);
    }

    [Fact]
    public void BuyingOutPricesThePileRatherThanTheFloorItAdvertises()
    {
        // The floor is a dump, but one unit of it: the rest of the stock under me sits just
        // below my own price, so taking the pile means paying nearly what it is worth. The
        // cheapest listing is a bad summary of a market here as everywhere else.
        var verdict = Read(4_400, Book(10d, Sold(4_400), (2_000, 1), (4_300, 30), (4_400, 5)));

        Assert.NotEqual(ChaseCall.BuyOut, verdict.Call);
        Assert.Equal(131_000, verdict.BuyOutCost);
        Assert.Equal(136_400, verdict.BuyOutBack);
    }

    [Fact]
    public void AThinQueueNobodyIsDumpingIntoIsWorthSittingOut()
    {
        // Two units under me on a board doing ten a day: they are gone this afternoon. The cut
        // is real but the queue is not, and 3,000 against sales at 4,000 is a bargain rather
        // than a panic, so there is nothing here to buy either.
        var verdict = Read(4_400, Book(10d, Sold(4_000), (3_000, 2), (4_400, 5)));

        Assert.Equal(ChaseCall.Wait, verdict.Call);
        Assert.Equal(1_405, verdict.Cut);
    }

    [Fact]
    public void ADeepPileOfCheapStockCannotBeBoughtOutOrWaitedOut()
    {
        // Four hundred units under me on a board doing ten a day is nearly six weeks of queue,
        // priced at half what the thing sells for. Neither waiting nor matching is the answer.
        var verdict = Read(4_000, Book(10d, Sold(4_000), (2_000, 400), (4_000, 5)));

        Assert.Equal(ChaseCall.Withdraw, verdict.Call);
    }

    [Fact]
    public void ADeepPileTheSalesAgreeWithIsSimplyTheNewPrice()
    {
        // The cheap stock is deep and things have been changing hands down there too. That is
        // not a dump, it is what the item now costs.
        var verdict = Read(4_000, Book(10d, Sold(2_100), (2_000, 400), (4_000, 5)));

        Assert.Equal(ChaseCall.Accept, verdict.Call);
    }

    [Fact]
    public void ABoardWhereNothingSellsCannotBeChasedDown()
    {
        var verdict = Read(4_000, Book(0d, Sold(4_000), (2_000, 2), (4_000, 5)));

        Assert.Equal(ChaseCall.Withdraw, verdict.Call);
    }

    [Fact]
    public void WithoutASaleRateThereIsNoArgumentToMake()
    {
        var book = Book(10d, Sold(4_000), (2_000, 2), (4_000, 5)).WithoutRate();
        var plan = Undercut.Of(4_000, book, 5);

        var verdict = Chase.Of(plan!.Value, book, MarketTax.None, Patience);

        Assert.Equal(ChaseCall.Follow, verdict.Call);
        Assert.Equal(2_005, verdict.Cut);
    }

    [Fact]
    public void TooFewSalesToJudgeIsNotEvidenceOfADump()
    {
        // Two sales at 4,000 is not enough to call 2,000 abnormal, so this stays the ordinary
        // "the queue is short, sit tight" rather than an invitation to spend gil on it.
        var verdict = Read(4_000, Book(10d, [4_000, 4_000], (2_000, 2), (4_000, 5)));

        Assert.Equal(ChaseCall.Wait, verdict.Call);
    }

    [Fact]
    public void OnlyStockAQualityBuyerWouldTakeIsPricedIntoTheBuyOut()
    {
        // Cheap NQ is not in front of an HQ listing, so it is neither a queue to jump nor
        // stock worth buying to clear one.
        var book = OrderBook.Create(
            1,
            [
                new Listing(515, 99, "Light"),
                new Listing(2_000, 2, "Light", IsHq: true),
                new Listing(4_989, 1, "Light", IsHq: true),
            ],
            10d,
            recentSales: Sold(4_989));

        var plan = Undercut.Of(4_989, book, 5, hq: true);
        var verdict = Chase.Of(plan!.Value, book, MarketTax.None, Patience, hq: true);

        Assert.Equal(ChaseCall.BuyOut, verdict.Call);
        Assert.Equal(2, verdict.UnitsUnder);
        Assert.Equal(4_000, verdict.BuyOutCost);
    }
}

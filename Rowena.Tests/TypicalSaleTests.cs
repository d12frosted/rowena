using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

/// <summary>
/// What people pay has to mean lately.
/// </summary>
/// <remarks>
/// The case that forced this: a parasol asking 340,989 against twenty sales that split into
/// an old cluster around 126,000 and a newer one at 300,000. A median over all twenty still
/// quoted the old cluster and called for repricing to half of what the last five buyers had
/// just paid. Robust against fire sales and blind to time is only half a virtue.
/// </remarks>
public class TypicalSaleTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static Sale[] Cluster(long price, int count, TimeSpan age) =>
        [.. Enumerable.Repeat(new Sale(price, Noon - age), count)];

    private static OrderBook Book(params Sale[][] sales) =>
        OrderBook.Create(
            1,
            [new Listing(1_000, 1, "Shiva")],
            retrieved: Noon,
            recentSales: [.. sales.SelectMany(cluster => cluster)]);

    [Fact]
    public void TheTypicalSaleIsWhatPeoplePaidLately()
    {
        var book = Book(
            Cluster(300, 5, TimeSpan.FromDays(2)),
            Cluster(100, 5, TimeSpan.FromDays(30)));

        Assert.Equal(300, book.TypicalSale);
    }

    [Fact]
    public void AThinWeekFallsBackToTheWholeList()
    {
        // Two sales are not enough to speak for the week; the whole list still speaks.
        var book = Book(
            Cluster(300, 2, TimeSpan.FromDays(2)),
            Cluster(100, 3, TimeSpan.FromDays(30)));

        Assert.Equal(100, book.TypicalSale);
    }

    [Fact]
    public void NoSalesIsNoAnswer()
    {
        Assert.Null(Book().TypicalSale);
    }

    [Fact]
    public void UndatedSalesStillSpeak()
    {
        // A book restored from an older cache, or a source that said nothing about when:
        // worse evidence than dated sales, but not no evidence.
        var book = OrderBook.Create(
            1,
            [new Listing(1_000, 1, "Shiva")],
            retrieved: Noon,
            recentSales: [new Sale(100, default), new Sale(120, default), new Sale(110, default)]);

        Assert.Equal(110, book.TypicalSale);
    }

    [Fact]
    public void TheRecentClusterLiftsTheVerdict()
    {
        // The parasol itself: cheapest on the board, asking within reason of what the last
        // week actually paid. The stale median called this "nobody pays"; lately says the
        // ask is merely ambitious, which is not a verdict.
        var book = OrderBook.Create(
            1,
            [new Listing(340_989, 1, "Shiva"), new Listing(340_994, 1, "Shiva"), new Listing(341_000, 1, "Shiva")],
            retrieved: Noon,
            recentSales:
            [
                .. Cluster(300_000, 5, TimeSpan.FromDays(2)),
                .. Cluster(126_000, 15, TimeSpan.FromDays(10)),
            ]);

        Assert.Null(Undercut.Of(340_989, book, 5));
    }

    [Fact]
    public void NobodyPaysIsMeasuredAgainstLately()
    {
        var book = OrderBook.Create(
            1,
            [new Listing(700_000, 1, "Shiva")],
            retrieved: Noon,
            recentSales:
            [
                .. Cluster(300_000, 5, TimeSpan.FromDays(2)),
                .. Cluster(126_000, 15, TimeSpan.FromDays(10)),
            ]);

        var plan = Undercut.Of(700_000, book, 5);

        Assert.Equal(UndercutWhy.NobodyPays, plan!.Value.Why);
        Assert.Equal(300_000, plan.Value.Below);
        Assert.Equal(299_995, plan.Value.Target);
    }
}

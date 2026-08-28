using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class BoardReadingTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static BoardOffer Offer(long price, int quantity = 1, bool hq = false) =>
        new(price, quantity, hq);

    /// <summary>A full page: ten offers climbing from the given price.</summary>
    private static BoardOffer[] FullPage(long from) =>
        [.. Enumerable.Range(0, BoardReading.PageSize).Select(step => Offer(from + step))];

    [Fact]
    public void PagesGatherIntoOneSortedBook()
    {
        var reading = new BoardReading(43598, "Shiva");

        reading.Add(FullPage(100));
        reading.Add([Offer(95, quantity: 3, hq: true), Offer(200)]);

        var book = reading.Book(Noon);

        Assert.Equal(12, book.Listings.Count);
        Assert.Equal(95, book.Floor);
        Assert.True(book.Listings[0].IsHq);
        Assert.All(book.Listings, listing => Assert.Equal("Shiva", listing.World));
        Assert.Equal(MarketSource.Game, book.Source);
        Assert.Equal(Noon, book.Retrieved);
    }

    [Fact]
    public void AShortPageEndsTheReading()
    {
        var reading = new BoardReading(1, "Shiva");

        reading.Add([Offer(100), Offer(101)]);

        Assert.True(reading.Ended);
        Assert.True(reading.Book(Noon).Complete);
    }

    [Fact]
    public void AnEmptyPageIsAnEmptyBoard()
    {
        var reading = new BoardReading(1, "Shiva");

        reading.Add([]);

        Assert.True(reading.Ended);
        Assert.Empty(reading.Book(Noon).Listings);
        Assert.True(reading.Book(Noon).Complete);
    }

    [Fact]
    public void AFullPageLeavesTheReadingOpen()
    {
        // Ten listings might be all there are or the first of many: the server does not say,
        // so whether to stop waiting is the caller's clock, not this class's.
        var reading = new BoardReading(1, "Shiva");

        reading.Add(FullPage(100));

        Assert.False(reading.Ended);
        Assert.True(reading.Book(Noon).Complete);
    }

    [Fact]
    public void AHundredListingsIsACutOff()
    {
        var reading = new BoardReading(1, "Shiva");

        for (var page = 0; page < 10; page++)
            reading.Add(FullPage(100 + (page * 10)));

        Assert.True(reading.Ended);
        Assert.False(reading.Book(Noon).Complete);
    }

    [Fact]
    public void PagesAfterTheEndAreSomebodyElses()
    {
        var reading = new BoardReading(1, "Shiva");

        reading.Add([Offer(100)]);
        reading.Add([Offer(50), Offer(60)]);

        Assert.Single(reading.Book(Noon).Listings);
    }

    [Fact]
    public void SalesBecomeTheRecentSales()
    {
        var reading = new BoardReading(1, "Shiva");

        Assert.False(reading.SalesSeen);

        reading.Add([Offer(100)]);
        reading.Sales([120, 110, 130]);

        Assert.True(reading.SalesSeen);
        Assert.Equal([120, 110, 130], reading.Book(Noon).RecentSales);
    }

    [Fact]
    public void TheRateStaysUnknown()
    {
        // The board says what is listed and what sold, never how fast it moves; the summary
        // imposes that afterwards, exactly as it does on a Universalis book.
        var reading = new BoardReading(1, "Shiva");

        reading.Add([Offer(100)]);
        reading.Sales([120]);

        Assert.False(reading.Book(Noon).RateKnown);
    }
}

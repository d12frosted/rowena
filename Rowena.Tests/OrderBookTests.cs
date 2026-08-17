using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class OrderBookTests
{
    private static OrderBook Book(params (long Price, int Quantity)[] listings) =>
        OrderBook.Create(1, listings.Select(l => new Listing(l.Price, l.Quantity, "Phoenix")));

    [Fact]
    public void ListingsAreSortedEvenWhenTheSourceIsNot()
    {
        var book = Book((300, 1), (100, 1), (200, 1));

        Assert.Equal([100, 200, 300], book.Listings.Select(listing => listing.UnitPrice));
        Assert.Equal(100, book.Floor);
    }

    [Fact]
    public void AnEmptyBookHasNoFloor()
    {
        var book = OrderBook.Empty(1);

        Assert.Null(book.Floor);
        Assert.Equal(0, book.UnitsListed);
    }

    [Fact]
    public void BuyingWithinOneListingPaysThatPrice()
    {
        var quote = Book((100, 10)).CostToBuy(3);

        Assert.True(quote.IsComplete);
        Assert.Equal(300, quote.Total);
        Assert.Equal(100, quote.WorstUnitPrice);
    }

    [Fact]
    public void BuyingAcrossListingsClimbsTheBook()
    {
        // Three at 100 then two at 250: five units cost 800, not five times the floor.
        var quote = Book((100, 3), (250, 5)).CostToBuy(5);

        Assert.True(quote.IsComplete);
        Assert.Equal(800, quote.Total);
        Assert.Equal(250, quote.WorstUnitPrice);
        Assert.Equal(160d, quote.AverageUnitPrice);
        Assert.Equal(300, quote.PremiumOverFloor(100));
    }

    [Fact]
    public void BuyingMoreThanIsListedReportsTheShortfall()
    {
        var quote = Book((100, 2)).CostToBuy(10);

        Assert.False(quote.IsComplete);
        Assert.Equal(2, quote.Filled);
        Assert.Equal(8, quote.ShortBy);
        Assert.Equal(200, quote.Total);
    }

    [Fact]
    public void BuyingNothingCostsNothing()
    {
        var quote = Book((100, 2)).CostToBuy(0);

        Assert.Equal(0, quote.Total);
        Assert.Equal(0, quote.Filled);
        Assert.Equal(0d, quote.AverageUnitPrice);
    }

    [Fact]
    public void UnitsAtOrBelowIgnoresDearerListings()
    {
        var book = Book((100, 3), (250, 5), (400, 2));

        Assert.Equal(3, book.UnitsAtOrBelow(100));
        Assert.Equal(8, book.UnitsAtOrBelow(250));
        Assert.Equal(10, book.UnitsAtOrBelow(999));
        Assert.Equal(0, book.UnitsAtOrBelow(99));
    }

    [Fact]
    public void TiersAccumulateByPrice()
    {
        var tiers = Book((100, 3), (100, 1), (250, 5)).Tiers();

        Assert.Equal(2, tiers.Count);
        Assert.Equal(new DepthTier(100, 4, 400), tiers[0]);
        Assert.Equal(new DepthTier(250, 9, 1_650), tiers[1]);
    }

    [Fact]
    public void AbsorptionIsUnknownWhenNothingSells()
    {
        Assert.Null(OrderBook.Create(1, [], saleVelocityPerDay: 0d).DaysToAbsorb(10));
        Assert.Equal(5d, OrderBook.Create(1, [], saleVelocityPerDay: 2d).DaysToAbsorb(10));
    }

    [Fact]
    public void WithoutCheapestTakesFromTheBottom()
    {
        var book = Book((100, 3), (250, 5)).WithoutCheapest(4);

        Assert.Equal(4, book.UnitsListed);
        Assert.Equal(250, book.Floor);
    }

    [Fact]
    public void WithoutCheapestKeepsThePartOfAListingItDidNotTake()
    {
        // Ten with three taken is seven, not nothing.
        var book = Book((100, 10)).WithoutCheapest(3);

        Assert.Equal(7, book.UnitsListed);
        Assert.Equal(100, book.Floor);
    }

    [Fact]
    public void WithoutCheapestCanEmptyTheBook()
    {
        Assert.Null(Book((100, 2)).WithoutCheapest(5).Floor);
    }

    [Fact]
    public void WithoutNothingChangesNothing()
    {
        Assert.Equal(2, Book((100, 2)).WithoutCheapest(0).UnitsListed);
        Assert.Equal(2, Book((100, 2)).WithoutCheapest(-1).UnitsListed);
    }

    [Fact]
    public void WithoutCheapestKeepsTheVelocity()
    {
        var book = OrderBook.Create(1, [new Listing(100, 5, "Phoenix")], saleVelocityPerDay: 4d);

        Assert.Equal(4d, book.WithoutCheapest(2).SaleVelocityPerDay);
    }

    [Fact]
    public void BuyingAHundredMountTokensCostsMoreThanTheFloorImplies()
    {
        // The finding this whole library exists for. The floor advertises one price; a
        // real hundred-unit order pays a blended price above it, and the gap is the
        // difference between a trade being worth taking and not.
        var book = Fixtures.Book(Fixtures.MountToken);
        var floor = book.Floor!.Value;

        var quote = book.CostToBuy(100);

        Assert.True(quote.Filled > 0);
        Assert.True(quote.Total > floor * quote.Filled);
        Assert.True(quote.AverageUnitPrice > floor);
        Assert.True(quote.WorstUnitPrice > floor);
        Assert.True(quote.PremiumOverFloor(floor) > 0);
    }
}

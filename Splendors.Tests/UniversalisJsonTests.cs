using Splendors.Core.Universalis;
using Xunit;

namespace Splendors.Tests;

public class UniversalisJsonTests
{
    [Fact]
    public void ParsesARecordedSingleItemResponse()
    {
        var book = Fixtures.Book(Fixtures.MountToken);

        Assert.Equal(41807u, book.ItemId);
        Assert.Equal(48_795, book.Floor);
        Assert.True(book.SaleVelocityPerDay > 0);
        Assert.NotEmpty(book.Listings);
        Assert.All(book.Listings, listing => Assert.True(listing.Quantity > 0));
    }

    [Fact]
    public void ListingsComeBackInPriceOrder()
    {
        var prices = Fixtures.Book(Fixtures.MountToken).Listings.Select(listing => listing.UnitPrice).ToArray();

        Assert.Equal(prices.OrderBy(price => price), prices);
    }

    [Fact]
    public void UploadTimeIsUsedAsTheSnapshotTime()
    {
        // How stale the data is matters, and the honest answer is when a player last saw
        // that board, not when we parsed the file.
        var book = Fixtures.Book(Fixtures.MountToken);

        Assert.NotEqual(default, book.Retrieved);
        Assert.True(book.Retrieved.Year > 2020);
    }

    [Fact]
    public void ParseItemsAcceptsTheSingleItemShapeToo()
    {
        // A one-id request to the comma-separated endpoint answers in the single-item
        // shape, so callers should not have to know which they are going to get.
        var books = UniversalisJson.ParseItems(Fixtures.Read(Fixtures.MountToken));

        Assert.Single(books);
        Assert.Equal(48_795, books[41807u].Floor);
    }

    [Fact]
    public void ParsesTheMountAsWell()
    {
        var book = Fixtures.Book(Fixtures.RroneekHorn);

        Assert.Equal(43598u, book.ItemId);
        Assert.Equal(6_199_934, book.Floor);
    }
}

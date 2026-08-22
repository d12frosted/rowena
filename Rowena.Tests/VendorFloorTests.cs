using Rowena.Core.Conversions;
using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class VendorFloorTests
{
    private static readonly Conversion Trade = new(
        "t",
        "t",
        [new ResourceAmount(Resource.Item(1, "input"), 1)],
        [new ResourceAmount(Resource.Item(9, "output"), 2)],
        "somewhere");

    private static Func<uint, OrderBook?> Books(params OrderBook[] books) =>
        id => books.FirstOrDefault(book => book.ItemId == id);

    private static OrderBook Listed(uint id, long price, double velocity = 1d) =>
        OrderBook.Create(id, [new Listing(price, 10, "Phoenix")], velocity);

    private static Func<uint, long> Vendor(long price) => id => id == 9 ? price : 0;

    [Fact]
    public void TheVendorWinsWhenTheBoardNetsLess()
    {
        // The board lists at 100 and keeps 5, so 95 a unit; a vendor pays 98 and keeps nothing.
        var quote = ConversionEvaluator.Evaluate(
            Trade, 1, Books(Listed(1, 10), Listed(9, 100)), MarketTax.Standard, Vendor(98));

        Assert.Equal(2 * 98, quote.NetProceeds);
        Assert.Contains(quote.Vendored, amount => amount.Resource.Id == 9);
        Assert.Equal(0d, quote.DaysToAbsorb);
    }

    [Fact]
    public void TheBoardWinsWhenItNetsMoreAndNothingIsVendored()
    {
        var quote = ConversionEvaluator.Evaluate(
            Trade, 1, Books(Listed(1, 10), Listed(9, 100)), MarketTax.Standard, Vendor(50));

        Assert.Equal(MarketTax.Standard.NetProceeds(200), quote.NetProceeds);
        Assert.Empty(quote.Vendored);
        Assert.Equal(2d, quote.DaysToAbsorb);
    }

    [Fact]
    public void NothingListedStillSellsToAVendor()
    {
        // An empty book is a fetched answer: nobody lists it. The vendor still buys it.
        var quote = ConversionEvaluator.Evaluate(
            Trade, 1, Books(Listed(1, 10), OrderBook.Empty(9)), MarketTax.Standard, Vendor(30));

        Assert.True(quote.IsExecutable);
        Assert.Equal(60, quote.NetProceeds);
        Assert.Contains(quote.Vendored, amount => amount.Resource.Id == 9);
    }

    [Fact]
    public void AnUnfetchedBookStaysUnpricedWhateverTheVendorPays()
    {
        // No book at all means no answer yet, and a vendor price must not paper over that:
        // the row would read as a confident loss until the fetch arrived.
        var quote = ConversionEvaluator.Evaluate(
            Trade, 1, Books(Listed(1, 10)), MarketTax.Standard, Vendor(30));

        Assert.False(quote.IsExecutable);
        Assert.Contains(quote.Unpriced, amount => amount.Resource.Id == 9);
    }

    [Fact]
    public void WithoutAVendorLookupNothingChanges()
    {
        var quote = ConversionEvaluator.Evaluate(
            Trade, 1, Books(Listed(1, 10), Listed(9, 100)), MarketTax.Standard);

        Assert.Equal(MarketTax.Standard.NetProceeds(200), quote.NetProceeds);
        Assert.Empty(quote.Vendored);
    }

    [Fact]
    public void ListingsUnderTheVendorPriceAreFreeGil()
    {
        // Three at 90 and two at 97 against a vendor paying 100. With the buyer's 5%, the
        // 90s cost 94 each (270 + 13 tax) and pay 300: 17 gained. The 97s cost 194 + 9 and
        // pay 200, a loss, so they are left.
        var book = OrderBook.Create(1, [new Listing(90, 3, "Phoenix"), new Listing(97, 2, "Phoenix")]);

        var found = VendorArbitrage.Find(book, vendorPrice: 100, MarketTax.Standard);

        Assert.Equal(3, found.Units);
        Assert.Equal(17, found.Profit);
    }

    [Fact]
    public void TheCheapFilterKeepsAnythingThatCouldPay()
    {
        // 90 plus 5% is 94, under the 100 a vendor pays: worth fetching in full.
        Assert.True(VendorArbitrage.Possible(90, 100, MarketTax.Standard));

        // 97 plus 5% is 101: the floor already loses, so nothing deeper can win.
        Assert.False(VendorArbitrage.Possible(97, 100, MarketTax.Standard));

        // Nothing listed, or nothing a vendor will take, is not a candidate either way.
        Assert.False(VendorArbitrage.Possible(0, 100, MarketTax.Standard));
        Assert.False(VendorArbitrage.Possible(50, 0, MarketTax.Standard));
    }

    [Fact]
    public void TheCheapFilterNeverDiscardsAWinner()
    {
        // The filter charges tax per unit where the board charges it per listing and floors
        // it, so it can only be too generous. Whatever Find would pay for, Possible keeps.
        for (long price = 1; price <= 200; price++)
        {
            for (var quantity = 1; quantity <= 5; quantity++)
            {
                var book = OrderBook.Create(1, [new Listing(price, quantity, "Phoenix")]);
                var found = VendorArbitrage.Find(book, vendorPrice: 100, MarketTax.Standard);

                if (found.Units > 0)
                    Assert.True(VendorArbitrage.Possible(price, 100, MarketTax.Standard), $"{quantity} at {price}");
            }
        }
    }

    [Fact]
    public void AFindIsSplitByTheWorldItStandsOn()
    {
        // Buying happens per world, by travelling to whoever is selling. A find spread over
        // three worlds is three trips, and the world holding the cheapest listing can hold
        // almost none of it: measured on a live board, one find showed five units on the
        // world it named and a hundred and forty-seven on another.
        var book = OrderBook.Create(1, [
            new Listing(10, 5, "Raiden"),
            new Listing(11, 147, "Lich"),
            new Listing(12, 20, "Phoenix"),
        ]);

        var found = VendorArbitrage.Find(book, vendorPrice: 100, MarketTax.None);

        Assert.Equal(172, found.Units);
        Assert.Equal("Lich", found.Best!.Value.World);
        Assert.Equal(147, found.Best!.Value.Units);
        Assert.Equal(3, found.ByWorld.Count);
        Assert.Equal(found.Profit, found.ByWorld.Sum(share => share.Profit));
        Assert.Equal(found.Units, found.ByWorld.Sum(share => share.Units));
    }

    [Fact]
    public void NoArbitrageWhenTheBoardIsDearer()
    {
        var book = OrderBook.Create(1, [new Listing(120, 3, "Phoenix")]);

        var found = VendorArbitrage.Find(book, vendorPrice: 100, MarketTax.Standard);

        Assert.Equal(0, found.Units);
        Assert.Equal(0, found.Profit);
    }
}

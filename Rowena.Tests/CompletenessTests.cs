using Rowena.Core.Conversions;
using Rowena.Core.Market;
using Rowena.Core.Universalis;
using Xunit;

namespace Rowena.Tests;

public class CompletenessTests
{
    [Fact]
    public void AResponseShorterThanTheCapIsTheWholeBook()
    {
        // Twenty listings came back where forty were allowed, so there are twenty.
        var book = UniversalisJson.ParseItem(Fixtures.Read(Fixtures.RroneekHorn), requested: 40);

        Assert.True(book.Complete);
    }

    [Fact]
    public void AResponseAtTheCapIsNotKnownToBeTheWholeBook()
    {
        // Forty came back and forty were allowed, so there may be a forty-first. Universalis
        // counts listingsCount and unitsForSale from what it returned, so neither can tell us.
        var book = UniversalisJson.ParseItem(Fixtures.Read(Fixtures.MountToken), requested: 40);

        Assert.False(book.Complete);
    }

    [Fact]
    public void WithoutACapNothingIsClaimedAboutCompleteness()
    {
        Assert.True(UniversalisJson.ParseItem(Fixtures.Read(Fixtures.MountToken)).Complete);
    }

    [Fact]
    public void RunningOutOfACompleteBookIsAShortfall()
    {
        var book = OrderBook.Create(1, [new Listing(100, 2, "Phoenix")]);

        var quote = book.CostToBuy(10, MarketTax.None);

        Assert.Equal(8, quote.ShortBy);
        Assert.False(quote.Uncertain);
    }

    [Fact]
    public void RunningOutOfATruncatedBookIsNotKnownToBeAShortfall()
    {
        // The listings we cannot see are the dearest ones, so what we can see is priced right
        // and what we cannot see is genuinely unknown. Calling it a shortfall would report the
        // board as unable to supply something it may well have.
        var book = OrderBook.Create(1, [new Listing(100, 2, "Phoenix")], complete: false);

        var quote = book.CostToBuy(10, MarketTax.None);

        Assert.True(quote.Uncertain);
        Assert.Equal(8, quote.ShortBy);
    }

    [Fact]
    public void StayingInsideATruncatedBookIsStillExact()
    {
        // Truncation drops the dearest listings, so an order that fits under them is priced
        // exactly. Only running past the end is unknown.
        var book = OrderBook.Create(1, [new Listing(100, 5, "Phoenix")], complete: false);

        var quote = book.CostToBuy(3, MarketTax.None);

        Assert.False(quote.Uncertain);
        Assert.True(quote.IsComplete);
        Assert.Equal(300, quote.Total);
    }

    [Fact]
    public void AQuoteSeparatesWhatIsMissingFromWhatIsMerelyUnseen()
    {
        var conversion = new Conversion(
            "t",
            "t",
            [new ResourceAmount(Resource.Item(1, "input"), 10)],
            [new ResourceAmount(Resource.Item(9, "output"), 1)],
            "somewhere");

        var truncated = OrderBook.Create(1, [new Listing(100, 2, "Phoenix")], complete: false);
        var output = OrderBook.Create(9, [new Listing(9_000, 5, "Phoenix")], saleVelocityPerDay: 1d);

        var quote = ConversionEvaluator.Evaluate(
            conversion, 1, id => id == 1 ? truncated : output, MarketTax.None);

        Assert.False(quote.IsExecutable);
        Assert.Empty(quote.Unsourced);
        Assert.Contains(quote.Unseen, amount => amount.Resource.Id == 1 && amount.Quantity == 8);
    }

    [Fact]
    public void AWholeBookThatRunsOutIsStillReportedAsShort()
    {
        var conversion = new Conversion(
            "t",
            "t",
            [new ResourceAmount(Resource.Item(1, "input"), 10)],
            [new ResourceAmount(Resource.Item(9, "output"), 1)],
            "somewhere");

        var complete = OrderBook.Create(1, [new Listing(100, 2, "Phoenix")]);
        var output = OrderBook.Create(9, [new Listing(9_000, 5, "Phoenix")], saleVelocityPerDay: 1d);

        var quote = ConversionEvaluator.Evaluate(
            conversion, 1, id => id == 1 ? complete : output, MarketTax.None);

        Assert.Contains(quote.Unsourced, amount => amount.Resource.Id == 1);
        Assert.Empty(quote.Unseen);
    }

    [Fact]
    public void ABookKnowsWhereItCameFrom()
    {
        Assert.Equal(MarketSource.Universalis, OrderBook.Create(1, []).Source);

        var fromGame = OrderBook.Create(1, [], source: MarketSource.Game);

        Assert.Equal(MarketSource.Game, fromGame.Source);
        Assert.True(fromGame.Complete);
    }
}

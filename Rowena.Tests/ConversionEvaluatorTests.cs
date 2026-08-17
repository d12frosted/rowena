using Rowena.Core.Conversions;
using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class ConversionEvaluatorTests
{
    private static readonly ConversionCatalog Catalog = ConversionCatalog.Default;

    private static readonly Resource Scrip = Catalog.ResourceFor("orange-gatherers-scrip");
    private static readonly Resource Token = Catalog.ResourceFor("mount-token");
    private static readonly Resource Mount = Catalog.ResourceFor("rroneek-horn");

    private static readonly OrderBook Tokens = Fixtures.Book(Fixtures.MountToken);
    private static readonly OrderBook Mounts = Fixtures.Book(Fixtures.RroneekHorn);

    private static Func<uint, OrderBook?> Books(params OrderBook[] books) =>
        id => books.FirstOrDefault(book => book.ItemId == id);

    private static ConversionQuote Quote(string id, int runs = 1) =>
        ConversionEvaluator.Evaluate(Catalog[id], runs, Books(Tokens, Mounts), MarketTax.Standard);

    [Fact]
    public void SellingScripsAsTokensCostsNoGilAndPricesTheScrip()
    {
        var quote = Quote("scrip-to-token");

        Assert.Equal(0, quote.GilOutlay);
        Assert.Equal(Tokens.Floor, quote.GrossProceeds);
        Assert.True(quote.NetProceeds < quote.GrossProceeds);
        Assert.True(quote.IsExecutable);

        var perScrip = quote.GilPer(Scrip);
        Assert.NotNull(perScrip);
        Assert.InRange(perScrip.Value, 40d, 50d);
    }

    [Fact]
    public void CarryingScripsAllTheWayToAMountBeatsSellingTheToken()
    {
        // The headline. Stopping at the intermediate is the obvious move and the worse
        // one, and the gap is large enough that it is worth the extra grind.
        var token = Quote("scrip-to-token").GilPer(Scrip);
        var mount = Quote("scrip-to-rroneek").GilPer(Scrip);

        Assert.NotNull(token);
        Assert.NotNull(mount);
        Assert.True(mount.Value > token.Value, $"mount {mount.Value:F2} should beat token {token.Value:F2}");
        Assert.True(mount.Value / token.Value > 1.1d, "expected the chain to be more than a rounding better");
    }

    [Fact]
    public void BuildingAMountFromBoughtTokensPaysAndTheDepthIsChargedFor()
    {
        var quote = Quote("tokens-to-rroneek");

        Assert.True(quote.IsExecutable, "the recorded book should cover a hundred tokens");
        Assert.True(quote.GilOutlay > 0);
        Assert.True(quote.Profit > 0);
        Assert.NotNull(quote.ReturnOnOutlay);
        Assert.True(quote.ReturnOnOutlay.Value > 0.05d);

        // Priced by walking the book, so the outlay exceeds the naive floor estimate.
        Assert.True(quote.GilOutlay > Tokens.Floor!.Value * 100);
    }

    [Fact]
    public void AbsorptionIsReportedAlongsideTheMargin()
    {
        // A margin you cannot sell into is not a margin, so the quote has to carry this.
        var quote = Quote("tokens-to-rroneek");

        Assert.NotNull(quote.DaysToAbsorb);
        Assert.True(quote.DaysToAbsorb.Value > 0);
    }

    [Fact]
    public void DepthCapsHowManyTimesTheTradeCanBeRun()
    {
        var size = ConversionEvaluator.LargestProfitableSize(
            Catalog["tokens-to-rroneek"],
            Books(Tokens, Mounts),
            MarketTax.Standard,
            cap: 20);

        Assert.True(size >= 1, "one mount should be affordable from the recorded book");
        Assert.True(size <= Tokens.UnitsListed / 100, "cannot run more times than there are tokens listed");
    }

    [Fact]
    public void InputsThatAreNotOnTheBoardAreReportedRatherThanPricedAtZero()
    {
        var quote = ConversionEvaluator.Evaluate(
            Catalog["tokens-to-rroneek"],
            1,
            Books(Mounts),
            MarketTax.Standard);

        Assert.False(quote.IsExecutable);
        Assert.Contains(quote.Unsourced, amount => amount.Resource == Token);
        Assert.Equal(0, quote.GilOutlay);
    }

    [Fact]
    public void OutputsThatAreNotOnTheBoardAreReportedRatherThanValuedAtZero()
    {
        var quote = ConversionEvaluator.Evaluate(
            Catalog["tokens-to-rroneek"],
            1,
            Books(Tokens),
            MarketTax.Standard);

        Assert.False(quote.IsExecutable);
        Assert.Contains(quote.Unpriced, amount => amount.Resource == Mount);
        Assert.Equal(0, quote.GrossProceeds);
    }

    [Fact]
    public void QuotesReportAbsoluteQuantitiesButKeepTheUnscaledRate()
    {
        var quote = Quote("scrip-to-token", runs: 3);

        Assert.Equal(3, quote.Runs);
        Assert.Equal(1_000, quote.Conversion.Consumes(Scrip));
        Assert.Equal(3_000, quote.CurrencySpent.Single(amount => amount.Resource == Scrip).Quantity);
    }
}

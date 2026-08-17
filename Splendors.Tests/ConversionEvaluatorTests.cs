using Splendors.Core.Conversions;
using Splendors.Core.Market;
using Xunit;

namespace Splendors.Tests;

public class ConversionEvaluatorTests
{
    private static readonly OrderBook Tokens = Fixtures.Book(Fixtures.MountToken);
    private static readonly OrderBook Mounts = Fixtures.Book(Fixtures.RroneekHorn);

    private static Func<uint, OrderBook?> Books(params OrderBook[] books) =>
        id => books.FirstOrDefault(book => book.ItemId == id);

    private static ConversionQuote Quote(Conversion conversion, int runs = 1) =>
        ConversionEvaluator.Evaluate(conversion, runs, Books(Tokens, Mounts), MarketTax.Standard);

    [Fact]
    public void SellingScripsAsTokensCostsNoGilAndPricesTheScrip()
    {
        var quote = Quote(ConversionCatalog.ScripToToken);

        Assert.Equal(0, quote.GilOutlay);
        Assert.Equal(Tokens.Floor, quote.GrossProceeds);
        Assert.True(quote.NetProceeds < quote.GrossProceeds);
        Assert.True(quote.IsExecutable);

        var perScrip = quote.GilPer(ConversionCatalog.OrangeGatherersScrip);
        Assert.NotNull(perScrip);
        Assert.InRange(perScrip.Value, 40d, 50d);
    }

    [Fact]
    public void CarryingScripsAllTheWayToAMountBeatsSellingTheToken()
    {
        // The headline. Stopping at the intermediate is the obvious move and the worse
        // one, and the gap is large enough that it is worth the extra grind.
        var token = Quote(ConversionCatalog.ScripToToken).GilPer(ConversionCatalog.OrangeGatherersScrip);
        var mount = Quote(ConversionCatalog.ScripToRroneek).GilPer(ConversionCatalog.OrangeGatherersScrip);

        Assert.NotNull(token);
        Assert.NotNull(mount);
        Assert.True(mount.Value > token.Value, $"mount {mount.Value:F2} should beat token {token.Value:F2}");
        Assert.True(mount.Value / token.Value > 1.1d, "expected the chain to be more than a rounding better");
    }

    [Fact]
    public void BuildingAMountFromBoughtTokensPaysAndTheDepthIsChargedFor()
    {
        var quote = Quote(ConversionCatalog.TokensToRroneek);

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
        var quote = Quote(ConversionCatalog.TokensToRroneek);

        Assert.NotNull(quote.DaysToAbsorb);
        Assert.True(quote.DaysToAbsorb.Value > 0);
    }

    [Fact]
    public void DepthCapsHowManyTimesTheTradeCanBeRun()
    {
        var size = ConversionEvaluator.LargestProfitableSize(
            ConversionCatalog.TokensToRroneek,
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
            ConversionCatalog.TokensToRroneek,
            1,
            Books(Mounts),
            MarketTax.Standard);

        Assert.False(quote.IsExecutable);
        Assert.Contains(quote.Unsourced, amount => amount.Resource == ConversionCatalog.MountToken);
        Assert.Equal(0, quote.GilOutlay);
    }

    [Fact]
    public void OutputsThatAreNotOnTheBoardAreReportedRatherThanValuedAtZero()
    {
        var quote = ConversionEvaluator.Evaluate(
            ConversionCatalog.TokensToRroneek,
            1,
            Books(Tokens),
            MarketTax.Standard);

        Assert.False(quote.IsExecutable);
        Assert.Contains(quote.Unpriced, amount => amount.Resource == ConversionCatalog.RroneekHorn);
        Assert.Equal(0, quote.GrossProceeds);
    }

    [Fact]
    public void QuotesReportAbsoluteQuantitiesButKeepTheUnscaledRate()
    {
        var quote = Quote(ConversionCatalog.ScripToToken, runs: 3);

        Assert.Equal(3, quote.Runs);
        Assert.Equal(1_000, quote.Conversion.Consumes(ConversionCatalog.OrangeGatherersScrip));
        Assert.Equal(
            3_000,
            quote.CurrencySpent.Single(amount => amount.Resource == ConversionCatalog.OrangeGatherersScrip).Quantity);
    }
}

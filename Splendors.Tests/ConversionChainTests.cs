using Splendors.Core.Conversions;
using Xunit;

namespace Splendors.Tests;

public class ConversionChainTests
{
    private static readonly Resource Scrip = Resource.Currency(1, "Scrip");
    private static readonly Resource Token = Resource.Item(2, "Token");
    private static readonly Resource Mount = Resource.Item(3, "Mount");
    private static readonly Resource Bonus = Resource.Item(4, "Bonus");

    private static Conversion Trade(string id, ResourceAmount input, params ResourceAmount[] outputs) =>
        new(id, id, [input], outputs, "somewhere");

    [Fact]
    public void ComposingMultipliesThroughTheLinkingRate()
    {
        var mint = Trade("mint", new ResourceAmount(Scrip, 1_000), new ResourceAmount(Token, 1));
        var redeem = Trade("redeem", new ResourceAmount(Token, 100), new ResourceAmount(Mount, 1));

        var chain = ConversionChain.Compose(mint, redeem, Token);

        Assert.Equal(100_000, chain.Consumes(Scrip));
        Assert.Equal(1, chain.Produces(Mount));
        Assert.Equal(0, chain.Consumes(Token));
        Assert.Equal(0, chain.Produces(Token));
    }

    [Fact]
    public void ComposingLeavesNothingOverWhenRatesDoNotDivide()
    {
        // Three per run into a counter that wants two: the lowest common multiple is six,
        // so two runs feed three redemptions and no stray units need explaining.
        var mint = Trade("mint", new ResourceAmount(Scrip, 10), new ResourceAmount(Token, 3));
        var redeem = Trade("redeem", new ResourceAmount(Token, 2), new ResourceAmount(Mount, 1));

        var chain = ConversionChain.Compose(mint, redeem, Token);

        Assert.Equal(20, chain.Consumes(Scrip));
        Assert.Equal(3, chain.Produces(Mount));
        Assert.Equal(0, chain.Produces(Token));
    }

    [Fact]
    public void SideProductsOfTheFirstStepSurvive()
    {
        var mint = Trade("mint", new ResourceAmount(Scrip, 10), new ResourceAmount(Token, 1), new ResourceAmount(Bonus, 2));
        var redeem = Trade("redeem", new ResourceAmount(Token, 5), new ResourceAmount(Mount, 1));

        var chain = ConversionChain.Compose(mint, redeem, Token);

        Assert.Equal(10, chain.Produces(Bonus));
        Assert.Equal(1, chain.Produces(Mount));
    }

    [Fact]
    public void ComposingThroughSomethingUnrelatedIsRejected()
    {
        var mint = Trade("mint", new ResourceAmount(Scrip, 10), new ResourceAmount(Token, 1));
        var redeem = Trade("redeem", new ResourceAmount(Token, 5), new ResourceAmount(Mount, 1));

        Assert.Throws<ArgumentException>(() => ConversionChain.Compose(mint, redeem, Bonus));
    }

    [Fact]
    public void TheCatalogueDerivesAHundredThousandScripsPerMount()
    {
        // Derived from the two published rates rather than typed in, so it cannot drift
        // away from them if either one is corrected.
        Assert.Equal(100_000, ConversionCatalog.ScripToRroneek.Consumes(ConversionCatalog.OrangeGatherersScrip));
        Assert.Equal(1, ConversionCatalog.ScripToRroneek.Produces(ConversionCatalog.RroneekHorn));
    }

    [Fact]
    public void ResourceIdentityIgnoresTheDisplayName()
    {
        // Otherwise the same item spelled two ways in two catalogue entries would fail to
        // link, and the composition would throw for no visible reason.
        Assert.Equal(Resource.Item(2, "Token"), Resource.Item(2, "Mount Token"));
        Assert.NotEqual(Resource.Item(2, "Token"), Resource.Currency(2, "Token"));
    }
}

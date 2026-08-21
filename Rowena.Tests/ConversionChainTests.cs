using Rowena.Core.Conversions;
using Xunit;

namespace Rowena.Tests;

public class ConversionScalingTests
{
    private static readonly Conversion Trade = new(
        "t",
        "t",
        [new ResourceAmount(Resource.Item(1, "in"), 3)],
        [new ResourceAmount(Resource.Item(9, "out"), 2)],
        "somewhere");

    [Fact]
    public void ScalingByOneChangesNothingAndAllocatesNothing()
    {
        // Every quote scales before pricing, and a quote is taken for every trade in the
        // catalogue several times a second. Scaling by one is identity, so it should hand
        // back what it was given rather than rebuilding both sides of it.
        Assert.Same(Trade, Trade.Scaled(1));
    }

    [Fact]
    public void ScalingMultipliesBothSides()
    {
        var three = Trade.Scaled(3);

        Assert.Equal(9, three.Inputs.Single().Quantity);
        Assert.Equal(6, three.Outputs.Single().Quantity);
    }
}

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
    public void TheLinkIsInferredWhenThereIsOnlyOne()
    {
        var mint = Trade("mint", new ResourceAmount(Scrip, 1_000), new ResourceAmount(Token, 1));
        var redeem = Trade("redeem", new ResourceAmount(Token, 100), new ResourceAmount(Mount, 1));

        Assert.Equal(100_000, ConversionChain.Compose(mint, redeem).Consumes(Scrip));
    }

    [Fact]
    public void AnAmbiguousLinkHasToBeNamed()
    {
        // Two candidates means guessing, and guessing here would silently price the wrong
        // chain. Better to refuse and make the catalogue say which.
        var mint = Trade("mint", new ResourceAmount(Scrip, 10), new ResourceAmount(Token, 1), new ResourceAmount(Bonus, 1));
        var redeem = new Conversion(
            "redeem",
            "redeem",
            [new ResourceAmount(Token, 1), new ResourceAmount(Bonus, 1)],
            [new ResourceAmount(Mount, 1)],
            "somewhere");

        var error = Assert.Throws<ArgumentException>(() => ConversionChain.Compose(mint, redeem));
        Assert.Contains("more than one", error.Message);
    }

    [Fact]
    public void UnrelatedConversionsDoNotCompose()
    {
        var mint = Trade("mint", new ResourceAmount(Scrip, 10), new ResourceAmount(Token, 1));
        var unrelated = Trade("unrelated", new ResourceAmount(Bonus, 5), new ResourceAmount(Mount, 1));

        Assert.Throws<ArgumentException>(() => ConversionChain.Compose(mint, unrelated));
        Assert.Throws<ArgumentException>(() => ConversionChain.Compose(mint, unrelated, Bonus));
    }

    [Fact]
    public void AChainOfThreeFoldsLeftToRight()
    {
        var gather = Trade("gather", new ResourceAmount(Scrip, 10), new ResourceAmount(Bonus, 1));
        var mint = Trade("mint", new ResourceAmount(Bonus, 2), new ResourceAmount(Token, 1));
        var redeem = Trade("redeem", new ResourceAmount(Token, 5), new ResourceAmount(Mount, 1));

        var chain = ConversionChain.Compose([gather, mint, redeem]);

        Assert.Equal(100, chain.Consumes(Scrip));
        Assert.Equal(1, chain.Produces(Mount));
    }

    [Fact]
    public void AnEmptyChainIsRejected()
    {
        Assert.Throws<ArgumentException>(() => ConversionChain.Compose([]));
    }

    [Fact]
    public void TheChainInheritsTheFirstStepsHandoff()
    {
        var mint = new Conversion(
            "mint",
            "mint",
            [new ResourceAmount(Scrip, 10)],
            [new ResourceAmount(Token, 1)],
            "somewhere",
            "gather-collectables");
        var redeem = Trade("redeem", new ResourceAmount(Token, 5), new ResourceAmount(Mount, 1));

        Assert.Equal("gather-collectables", ConversionChain.Compose(mint, redeem).Handoff);
        Assert.Null(ConversionChain.Compose(redeem, Trade("resell", new ResourceAmount(Mount, 1), new ResourceAmount(Bonus, 1))).Handoff);
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

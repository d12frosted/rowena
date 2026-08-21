using Rowena.Core.Conversions;
using Xunit;

namespace Rowena.Tests;

public class CatalogMergeTests
{
    private static readonly Resource Scrip = Resource.Currency(41785, "Orange Gatherers' Scrip");
    private static readonly Resource Token = Resource.Item(41807, "Mount Token");
    private static readonly Resource Horn = Resource.Item(43598, "Rroneek Horn");

    private static Conversion Trade(
        string id,
        (Resource Resource, int Quantity)[] inputs,
        (Resource Resource, int Quantity)[] outputs,
        string venue = "somewhere") =>
        new(
            id,
            id,
            [.. inputs.Select(input => new ResourceAmount(input.Resource, input.Quantity))],
            [.. outputs.Select(output => new ResourceAmount(output.Resource, output.Quantity))],
            venue);

    [Fact]
    public void GeneratedTradesJoinTheHandWrittenOnes()
    {
        var hand = Trade("hand", [(Scrip, 1_000)], [(Token, 1)]);
        var generated = Trade("generated", [(Token, 100)], [(Horn, 1)]);

        var merged = CatalogMerge.Merge([hand], [generated]);

        Assert.Equal(["hand", "generated"], merged.Select(conversion => conversion.Id));
    }

    [Fact]
    public void TheHandWrittenCopyOfTheSameTradeWins()
    {
        // Same rate, different everything else: the hand-written entry carries the venue and
        // handoff someone bothered to type, so it is the one to keep.
        var hand = Trade("hand", [(Scrip, 1_000)], [(Token, 1)], venue: "Scrip Exchange");
        var generated = Trade("generated", [(Scrip, 1_000)], [(Token, 1)], venue: "shop 1770786");

        var merged = CatalogMerge.Merge([hand], [generated]);

        var only = Assert.Single(merged);
        Assert.Equal("hand", only.Id);
        Assert.Equal("Scrip Exchange", only.Venue);
    }

    [Fact]
    public void ADifferentRateIsADifferentTrade()
    {
        var hand = Trade("hand", [(Scrip, 1_000)], [(Token, 1)]);
        var generated = Trade("generated", [(Scrip, 500)], [(Token, 1)]);

        Assert.Equal(2, CatalogMerge.Merge([hand], [generated]).Count);
    }

    [Fact]
    public void SidesAreComparedAsSetsNotSequences()
    {
        var hand = Trade("hand", [(Scrip, 1_000), (Token, 1)], [(Horn, 1)]);
        var generated = Trade("generated", [(Token, 1), (Scrip, 1_000)], [(Horn, 1)]);

        var only = Assert.Single(CatalogMerge.Merge([hand], [generated]));
        Assert.Equal("hand", only.Id);
    }

    [Fact]
    public void DuplicatesAmongTheGeneratedCollapseToo()
    {
        // Two NPCs offering the same exchange is one opportunity, not two rows.
        var first = Trade("gen-1", [(Scrip, 1_000)], [(Token, 1)]);
        var second = Trade("gen-2", [(Scrip, 1_000)], [(Token, 1)]);

        var only = Assert.Single(CatalogMerge.Merge([], [first, second]));
        Assert.Equal("gen-1", only.Id);
    }
}

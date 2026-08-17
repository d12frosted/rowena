using Rowena.Core.Conversions;
using Xunit;

namespace Rowena.Tests;

public class ConversionCatalogTests
{
    private const string Minimal = """
        {
          "resources": {
            "scrip": { "kind": "currency", "id": 41785, "name": "Orange Gatherers' Scrip" },
            "token": { "kind": "item", "id": 41807, "name": "Mount Token" },
            "mount": { "kind": "item", "id": 43598, "name": "Rroneek Horn" }
          },
          "conversions": [
            { "id": "mint",   "venue": "Scrip Exchange",
              "inputs":  [ { "resource": "scrip", "quantity": 1000 } ],
              "outputs": [ { "resource": "token", "quantity": 1 } ] },
            { "id": "redeem", "venue": "Splendors Vendor",
              "inputs":  [ { "resource": "token", "quantity": 100 } ],
              "outputs": [ { "resource": "mount", "quantity": 1 } ] }
          ],
          "chains": [
            { "id": "all-the-way", "name": "Gather through to a mount", "steps": [ "mint", "redeem" ] }
          ]
        }
        """;

    private static string WithoutChains => Minimal.Replace("\"chains\"", "\"unusedChains\"");

    [Fact]
    public void LoadsResourcesWithTheirKinds()
    {
        var catalog = ConversionCatalog.Load(Minimal);

        Assert.Equal(ResourceKind.Currency, catalog.ResourceFor("scrip").Kind);
        Assert.Equal(ResourceKind.Item, catalog.ResourceFor("token").Kind);
        Assert.Equal(41807u, catalog.ResourceFor("token").Id);
        Assert.Equal("Mount Token", catalog.ResourceFor("token").Name);
    }

    [Fact]
    public void ChainsAreComposedAtLoadTime()
    {
        var catalog = ConversionCatalog.Load(Minimal);
        var chain = catalog["all-the-way"];

        Assert.Equal(100_000, chain.Consumes(catalog.ResourceFor("scrip")));
        Assert.Equal(1, chain.Produces(catalog.ResourceFor("mount")));
        Assert.Equal("Gather through to a mount", chain.Name);
        Assert.Equal("all-the-way", chain.Id);
    }

    [Fact]
    public void ANameIsOptionalAndFallsBackToTheId()
    {
        Assert.Equal("mint", ConversionCatalog.Load(Minimal)["mint"].Name);
    }

    [Fact]
    public void MissingChainsAreFine()
    {
        var catalog = ConversionCatalog.Load(WithoutChains);

        Assert.Equal(2, catalog.Conversions.Count);
        Assert.False(catalog.TryGetConversion("all-the-way", out _));
    }

    [Theory]
    [InlineData("\"resource\": \"token\", \"quantity\": 100", "\"resource\": \"nope\", \"quantity\": 100", "nope")]
    [InlineData("\"steps\": [ \"mint\", \"redeem\" ]", "\"steps\": [ \"mint\", \"nope\" ]", "nope")]
    [InlineData("\"kind\": \"currency\"", "\"kind\": \"vibes\"", "vibes")]
    [InlineData("\"quantity\": 1000", "\"quantity\": 0", "quantity 0")]
    public void MalformedCataloguesFailLoudlyAndSayWhy(string from, string to, string expected)
    {
        // A catalogue that silently dropped half its entries would read as "nothing is
        // worth doing", which is the most expensive way for this to be wrong.
        var error = Assert.Throws<InvalidDataException>(() => ConversionCatalog.Load(Minimal.Replace(from, to)));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void ARepeatedIdIsRejected()
    {
        var doubled = Minimal.Replace("\"id\": \"redeem\"", "\"id\": \"mint\"");

        Assert.Throws<InvalidDataException>(() => ConversionCatalog.Load(doubled));
    }

    [Fact]
    public void AChainCannotShadowAConversion()
    {
        var clashing = Minimal.Replace("\"id\": \"all-the-way\"", "\"id\": \"mint\"");

        Assert.Throws<InvalidDataException>(() => ConversionCatalog.Load(clashing));
    }

    [Fact]
    public void NonsenseIsNotAStackTrace()
    {
        Assert.Throws<InvalidDataException>(() => ConversionCatalog.Load("{ this is not json"));
    }

    [Fact]
    public void TheEmbeddedCatalogueLoadsAndDerivesTheMountRate()
    {
        // The catalogue that actually ships. If the embedded resource were missing or the
        // rates stopped composing, everything downstream would quietly price nothing.
        var catalog = ConversionCatalog.Default;

        Assert.Equal(
            100_000,
            catalog["scrip-to-rroneek"].Consumes(catalog.ResourceFor("orange-gatherers-scrip")));
        Assert.Equal(1, catalog["scrip-to-rroneek"].Produces(catalog.ResourceFor("rroneek-horn")));
        Assert.Equal(1_000, catalog["scrip-to-token"].Consumes(catalog.ResourceFor("orange-gatherers-scrip")));
        Assert.Equal(100, catalog["tokens-to-barreltender"].Consumes(catalog.ResourceFor("mount-token")));
    }

    private static string WithHandoff => Minimal.Replace(
        "\"venue\": \"Scrip Exchange\",",
        "\"venue\": \"Scrip Exchange\", \"handoff\": \"gather-collectables\",");

    [Fact]
    public void AHandoffHintIsReadAndIsOptional()
    {
        var catalog = ConversionCatalog.Load(WithHandoff);

        Assert.Equal("gather-collectables", catalog["mint"].Handoff);
        Assert.Null(catalog["redeem"].Handoff);
    }

    [Fact]
    public void AChainTakesItsHandoffFromTheFirstStep()
    {
        // A chain's inputs are earned by its first step, so that is where the hint belongs.
        // Later steps only spend what the first one produced.
        Assert.Equal("gather-collectables", ConversionCatalog.Load(WithHandoff)["all-the-way"].Handoff);
    }

    [Fact]
    public void TheShippedCatalogueSaysHowScripsAreEarned()
    {
        var catalog = ConversionCatalog.Default;

        Assert.Equal("gather-collectables", catalog["scrip-to-token"].Handoff);
        Assert.Equal("gather-collectables", catalog["scrip-to-rroneek"].Handoff);

        // Buying tokens off the board is not something anyone can be sent to gather.
        Assert.Null(catalog["tokens-to-rroneek"].Handoff);
    }

    [Fact]
    public void AskingForSomethingAbsentSaysSo()
    {
        var catalog = ConversionCatalog.Default;

        Assert.Throws<KeyNotFoundException>(() => catalog["no-such-conversion"]);
        Assert.Throws<KeyNotFoundException>(() => catalog.ResourceFor("no-such-resource"));
    }
}

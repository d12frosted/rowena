using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class KnownListingsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static SeenRetainer Retainer(ulong id, DateTimeOffset at, params MarketSlot[] slots) =>
        new(id, $"r{id}", CityId: 3, at, slots);

    private static RetainerListing Mine(ulong retainer, uint item, long price, int units) =>
        new(retainer, $"r{retainer}", 3, item, price, units, false);

    [Fact]
    public void TheSlotsAreTheWholeAnswerWhenTheBoardHasNotBeenAsked()
    {
        // The bug this exists for: sixty listings across three retainers, eleven on the board
        // because only eleven items had been searched for.
        var listings = KnownListings.Merge(
            [
                Retainer(1, Noon, new MarketSlot(10, 5, 100), new MarketSlot(11, 1, 900)),
                Retainer(2, Noon, new MarketSlot(10, 2, 95)),
            ],
            []);

        Assert.Equal(3, listings.Count);
        Assert.Equal(2, listings.Count(listing => listing.ItemId == 10));
    }

    [Fact]
    public void EmptySlotsAreNotListings()
    {
        var listings = KnownListings.Merge([Retainer(1, Noon, default, new MarketSlot(10, 5, 100), default)], []);

        Assert.Single(listings);
    }

    [Fact]
    public void ANewerBoardViewReplacesThatRetainersListingsOfThatItem()
    {
        // The retainer was opened at noon with five out; the board at one showed two of mine
        // left. Three sold in between and the board is the later word.
        var listings = KnownListings.Merge(
            [Retainer(1, Noon, new MarketSlot(10, 5, 100), new MarketSlot(11, 1, 900))],
            [new BoardSighting(10, Noon.AddHours(1), [Mine(1, 10, 100, 2)])]);

        var ten = Assert.Single(listings, listing => listing.ItemId == 10);
        Assert.Equal(2, ten.Quantity);
        Assert.Contains(listings, listing => listing.ItemId == 11);
    }

    [Fact]
    public void ANewerBoardViewWithNoneOfMineMeansItSold()
    {
        var listings = KnownListings.Merge(
            [Retainer(1, Noon, new MarketSlot(10, 5, 100))],
            [new BoardSighting(10, Noon.AddHours(1), [])]);

        Assert.Empty(listings);
    }

    [Fact]
    public void AnOlderBoardViewIsOverruledByTheSlots()
    {
        // Searched at eleven, found none of mine; listed it at noon and opened the retainer.
        var listings = KnownListings.Merge(
            [Retainer(1, Noon, new MarketSlot(10, 5, 100))],
            [new BoardSighting(10, Noon.AddHours(-1), [])]);

        Assert.Single(listings);
    }

    [Fact]
    public void ABoardViewOnlySpeaksForTheRetainersItIsNewerThan()
    {
        // Retainer 1 seen at noon, retainer 2 seen at two. The board at one says retainer 1
        // has none left and says nothing about retainer 2, whose slots are the later word.
        var listings = KnownListings.Merge(
            [
                Retainer(1, Noon, new MarketSlot(10, 5, 100)),
                Retainer(2, Noon.AddHours(2), new MarketSlot(10, 3, 100)),
            ],
            [new BoardSighting(10, Noon.AddHours(1), [])]);

        var left = Assert.Single(listings);
        Assert.Equal(2ul, left.RetainerId);
    }

    [Fact]
    public void ARetainerNeverOpenedIsStillKnownFromTheBoard()
    {
        var listings = KnownListings.Merge(
            [Retainer(1, Noon, new MarketSlot(10, 5, 100))],
            [new BoardSighting(10, Noon.AddHours(1), [Mine(1, 10, 100, 5), Mine(9, 10, 90, 1)])]);

        Assert.Equal(2, listings.Count);
        Assert.Contains(listings, listing => listing.RetainerId == 9);
    }

    [Fact]
    public void TheRetainersNameAndCityComeFromTheRetainer()
    {
        var listings = KnownListings.Merge(
            [new SeenRetainer(1, "Ada", 7, Noon, [new MarketSlot(10, 5, 100, IsHq: true)])],
            []);

        var listing = Assert.Single(listings);
        Assert.Equal("Ada", listing.Retainer);
        Assert.Equal(7u, listing.CityId);
        Assert.True(listing.IsHq);
        Assert.Equal(100, listing.UnitPrice);
    }
}

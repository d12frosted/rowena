using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class GatherPlanTests
{
    /// <summary>Room for a thousand, so the board never binds and the aim is what is on trial.</summary>
    private static GatherCandidate Plenty(uint id, long net, bool timed = false) =>
        new(id, net, 1000, 0, timed);

    [Fact]
    public void TheDearestThingComesFirst()
    {
        var basket = GatherPlan.For([Plenty(1, 100), Plenty(2, 5000)], capacity: 50, horizonDays: 7);

        Assert.Equal(2u, basket[0].ItemId);
        Assert.Equal(50, basket[0].Units);
        Assert.Equal(250_000, GatherPlan.Worth(basket));
    }

    [Fact]
    public void ItStopsAtWhatTheBoardWillTake()
    {
        // The whole point: gathering two hundred of something that sells four a day leaves
        // most of the pile sitting in a retainer.
        var basket = GatherPlan.For(
            [new GatherCandidate(1, 5000, 4, 0), new GatherCandidate(2, 100, 1000, 0)],
            capacity: 200,
            horizonDays: 7);

        Assert.Equal(28, basket[0].Units);
        Assert.Equal(172, basket[1].Units);
    }

    [Fact]
    public void StockAlreadyListedIsAheadOfYours()
    {
        var basket = GatherPlan.For(
            [new GatherCandidate(1, 5000, 10, 50), new GatherCandidate(2, 100, 10, 0)],
            capacity: 100,
            horizonDays: 7);

        Assert.Equal(20, basket[0].Units);
        Assert.Equal(70, basket[1].Units);
    }

    [Fact]
    public void AMarketAlreadyOversuppliedIsSkipped()
    {
        var basket = GatherPlan.For(
            [new GatherCandidate(1, 99_999, 10, 500), new GatherCandidate(2, 100, 10, 0)],
            capacity: 50,
            horizonDays: 7);

        Assert.Single(basket);
        Assert.Equal(2u, basket[0].ItemId);
    }

    [Fact]
    public void ADeadMarketIsNotWorthGathering()
    {
        var basket = GatherPlan.For(
            [new GatherCandidate(1, 999_999, 0, 0), Plenty(2, 10)],
            capacity: 20,
            horizonDays: 7);

        Assert.Single(basket);
        Assert.Equal(2u, basket[0].ItemId);
    }

    [Fact]
    public void NoTimeIsNoPlan() =>
        Assert.Empty(GatherPlan.For([Plenty(1, 100)], capacity: 0, horizonDays: 7));

    // ---- what the aim changes

    [Fact]
    public void MostGilWillHappilyPutEverythingInOneThing()
    {
        var basket = GatherPlan.For(
            [Plenty(1, 5000), Plenty(2, 4000), Plenty(3, 3000)],
            capacity: 100,
            horizonDays: 7,
            GatherAim.MostGil);

        Assert.Single(basket);
        Assert.Equal(100, basket[0].Units);
    }

    [Fact]
    public void AMixedBagRefusesToBeOneThing()
    {
        // One price move should not take the whole trip with it, so a quarter each is the most
        // any one of them gets.
        var basket = GatherPlan.For(
            [Plenty(1, 5000), Plenty(2, 4000), Plenty(3, 3000), Plenty(4, 2000), Plenty(5, 1000)],
            capacity: 100,
            horizonDays: 7,
            GatherAim.MixedBag);

        Assert.Equal(4, basket.Count);
        Assert.All(basket, portion => Assert.Equal(25, portion.Units));
    }

    [Fact]
    public void AMixedBagEarnsLessThanChasingTheTop()
    {
        GatherCandidate[] candidates = [Plenty(1, 5000), Plenty(2, 100), Plenty(3, 100), Plenty(4, 100)];

        var most = GatherPlan.For(candidates, 100, 7, GatherAim.MostGil);
        var mixed = GatherPlan.For(candidates, 100, 7, GatherAim.MixedBag);

        Assert.True(GatherPlan.Worth(mixed) < GatherPlan.Worth(most));
    }

    [Fact]
    public void SellsSoonestGathersOnlyWhatTomorrowWillTake()
    {
        // Dear and slow against dear and quick. The dearer one still leads, but by tomorrow the
        // board only wants five of it, so the rest of the trip goes to the one that moves.
        GatherCandidate[] candidates = [new(1, 5000, 5, 0), new(2, 4000, 500, 0)];

        var soon = GatherPlan.For(candidates, capacity: 100, horizonDays: 7, GatherAim.SellsSoonest);

        Assert.Equal(1u, soon[0].ItemId);
        Assert.Equal(5, soon[0].Units);
        Assert.Equal(95, soon[1].Units);

        // Given the week, the same trip takes seven times as much of the slow one.
        Assert.Equal(35, GatherPlan.For(candidates, capacity: 100, horizonDays: 7)[0].Units);
    }

    [Fact]
    public void SellsSoonestIsStillAboutGilRatherThanTurnover()
    {
        // What sent this back for a second look: ranked on turnover it bought three hundred
        // fifty-gil crystals, because the board moves seventy-five thousand a day and nothing
        // else could outrank that. A good haul with none of it waiting, not a fast worthless one.
        var basket = GatherPlan.For(
            [new GatherCandidate(1, 50, 75_000, 0), new GatherCandidate(2, 4000, 500, 0)],
            capacity: 300,
            horizonDays: 7,
            GatherAim.SellsSoonest);

        Assert.Equal(2u, basket[0].ItemId);
        Assert.True(GatherPlan.Worth(basket) > 1_000_000);
    }

    // ---- nodes on a clock

    [Fact]
    public void AWindowfulCostsTheTripRatherThanTheHour()
    {
        // Forty items for twenty-five items' worth of time is a bargain, which is the whole
        // reason timed nodes are worth the detour, so it goes first despite paying less each.
        var basket = GatherPlan.For(
            [Plenty(1, 100), Plenty(2, 90, timed: true)],
            capacity: 100,
            horizonDays: 7,
            windowYield: 40,
            windowCost: 25);

        Assert.Equal(2u, basket[0].ItemId);
        Assert.Equal(40, basket[0].Units);
        Assert.Equal(25d, basket[0].Cost);
    }

    [Fact]
    public void AWindowIsAllOfItOrNoneOfIt()
    {
        // You cannot visit three quarters of a node, and half a trip costs the same as all of it.
        var basket = GatherPlan.For(
            [Plenty(1, 10_000, timed: true)],
            capacity: 10,
            horizonDays: 7,
            windowYield: 40,
            windowCost: 25);

        Assert.Empty(basket);
    }

    [Fact]
    public void AWindowIsCutShortByAMarketWithLittleRoom()
    {
        var basket = GatherPlan.For(
            [new GatherCandidate(1, 5000, 2, 0, true)],
            capacity: 100,
            horizonDays: 7,
            windowYield: 40,
            windowCost: 25);

        Assert.Equal(14, basket[0].Units);
    }

    [Fact]
    public void ATenMinuteTripCanStillBeWorthOneWindow()
    {
        // What sent this back for a second look: timed nodes were dropped outright, and a short
        // session is exactly when one window is most of what you can do.
        var basket = GatherPlan.For(
            [Plenty(1, 200), Plenty(2, 3000, timed: true)],
            capacity: 50,
            horizonDays: 7,
            windowYield: 40,
            windowCost: 25);

        Assert.Equal(2u, basket[0].ItemId);
        Assert.Equal(2, basket.Count);
        Assert.Equal(25, basket[1].Units);
    }
}

public class GatherStockTests
{
    [Fact]
    public void WhatIAlreadyHoldUsesUpTheRoomBeforeGatheringDoes()
    {
        // The board takes ten a day, nobody has any listed, and I have forty in a retainer.
        // A week's room is seventy; forty of it is already mine.
        var basket = GatherPlan.For(
            [new GatherCandidate(1, 5000, 10, 0, Held: 40), new GatherCandidate(2, 100, 10, 0)],
            capacity: 100,
            horizonDays: 7);

        Assert.Equal(30, basket[0].Units);
        Assert.Equal(70, basket[1].Units);
    }

    [Fact]
    public void APileThatOutlastsTheHorizonIsNotWorthAddingTo()
    {
        // Nine hundred and ninety-nine of something that sells twelve a day is eighty days of
        // stock. Gathering more of it is gathering for October.
        var basket = GatherPlan.For(
            [new GatherCandidate(1, 99_999, 12, 0, Held: 999), new GatherCandidate(2, 100, 10, 0)],
            capacity: 50,
            horizonDays: 7);

        Assert.Single(basket);
        Assert.Equal(2u, basket[0].ItemId);
    }

    [Fact]
    public void BacklogIsHowLongTheBoardNeedsForWhatIAlreadyHave()
    {
        Assert.Equal(83.25d, GatherPlan.Backlog(held: 999, listedMine: 0, salesPerDay: 12)!.Value, 2);
        Assert.Equal(10d, GatherPlan.Backlog(held: 50, listedMine: 50, salesPerDay: 10));
        Assert.Null(GatherPlan.Backlog(held: 50, listedMine: 0, salesPerDay: 0));
        Assert.Equal(0d, GatherPlan.Backlog(held: 0, listedMine: 0, salesPerDay: 10));
    }
}

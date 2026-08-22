using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class CraftShortlistTests
{
    private static Candidate Busy(string id, double revenue) => new(id, revenue, 1000, 50);

    /// <summary>Dear and quiet, so its turnover is real but modest: half a sale a day of it.</summary>
    private static Candidate Slow(string id, long floor) => new(id, floor * 0.5, floor, 0.5);

    [Fact]
    public void TheBusiestMarketsAreTakenFirst()
    {
        var picked = CraftShortlist.Pick(
            [Busy("a", 100), Busy("b", 900), Busy("c", 500)],
            busy: 2,
            niche: 0,
            slowPerDay: 2);

        Assert.Equal(["b", "c"], picked);
    }

    [Fact]
    public void ANicheCannotBeReachedByRankingOnRevenue()
    {
        // The point of the reserved slots. A thing selling once a day for two million turns
        // over less than a trinket selling three hundred times, so ranking on revenue alone
        // never costs it and nobody ever finds out what it pays.
        var picked = CraftShortlist.Pick(
            [Busy("a", 900), Busy("b", 800), Slow("rare", 2_000_000)],
            busy: 2,
            niche: 1,
            slowPerDay: 2);

        Assert.Contains("rare", picked);
    }

    [Fact]
    public void TheDearestSlowThingsAreTheOnesWorthCosting()
    {
        var picked = CraftShortlist.Pick(
            [Slow("cheap", 100), Slow("dear", 900_000), Slow("middling", 5_000)],
            busy: 0,
            niche: 2,
            slowPerDay: 2);

        Assert.Equal(["dear", "middling"], picked);
    }

    [Fact]
    public void SomethingAlreadyTakenForBeingBusyIsNotTakenTwice()
    {
        var picked = CraftShortlist.Pick(
            [Busy("a", 9_000_000), Slow("rare", 500_000)],
            busy: 2,
            niche: 2,
            slowPerDay: 2);

        Assert.Equal(2, picked.Count);
        Assert.Equal(["a", "rare"], picked);
    }

    [Fact]
    public void SomethingNobodyBuysIsNotANiche()
    {
        // No sales at all is a shut market, not a quiet one, and the reserved slots are worth
        // more than the dearest thing nobody has ever bought.
        var picked = CraftShortlist.Pick(
            [new Candidate("dead", 0, 9_000_000, 0), Slow("quiet", 1000)],
            busy: 0,
            niche: 2,
            slowPerDay: 2);

        Assert.Equal(["quiet"], picked);
    }

    [Fact]
    public void TheDearestThingNobodyBuysDoesNotWinAReservedSlot()
    {
        // Ranked on price alone the reserved slots fill with parked items: something listed at
        // a billion that has barely ever sold is the dearest thing on the board and the least
        // worth costing. A survey has no sale history to catch a fantasy price with, but it can
        // tell a market that barely moves from one that does not move at all.
        var picked = CraftShortlist.Pick(
            [new Candidate("parked", 30_000, 999_999_999, 0.03), Slow("real", 40_000)],
            busy: 0,
            niche: 2,
            slowPerDay: 2);

        Assert.Equal(["real"], picked);
    }

    [Fact]
    public void AFastMarketIsNotANicheHoweverDearItIs()
    {
        var picked = CraftShortlist.Pick(
            [new Candidate("busy", 999_999, 9_000_000, 80)],
            busy: 0,
            niche: 2,
            slowPerDay: 2);

        Assert.Empty(picked);
    }

    [Fact]
    public void NoReservedSlotsIsTheOldBehaviour()
    {
        var picked = CraftShortlist.Pick(
            [Busy("a", 9_000_000), Slow("rare", 5_000_000)],
            busy: 1,
            niche: 0,
            slowPerDay: 2);

        Assert.Equal(["a"], picked);
    }
}

using Rowena.Core.Market;
using Xunit;

namespace Rowena.Tests;

public class GatherPaceTests
{
    private const double Gap = 300;

    [Fact]
    public void NothingMeasuredYetHasNoOpinion()
    {
        var tally = default(GatherTally);

        Assert.Null(tally.PerHour);
    }

    [Fact]
    public void TwoGathersAMinuteApartMeasureTheMinuteBetweenThem()
    {
        var tally = GatherPace.Add(default, items: 5, secondsSinceLast: 60, Gap);

        Assert.Equal(5, tally.Items);
        Assert.Equal(60, tally.Seconds);
    }

    [Fact]
    public void ALongGapStartsTheClockRatherThanCountingTheGap()
    {
        // Standing at a node for an hour then gathering does not make the rate one an hour,
        // and counting the wait would say exactly that.
        var tally = GatherPace.Add(default, items: 5, secondsSinceLast: 4000, Gap);

        Assert.Equal(0, tally.Items);
        Assert.Equal(0, tally.Seconds);
    }

    [Fact]
    public void TravelBetweenNodesIsPartOfTheRate()
    {
        // Not the rate while standing at a node, which nobody sustains: an hour of gathering
        // is mostly getting to the next one, so the time between gathers counts.
        var tally = default(GatherTally);

        for (var node = 0; node < 10; node++)
            tally = GatherPace.Add(tally, items: 40, secondsSinceLast: 120, Gap);

        Assert.Equal(1200, tally.PerHour!.Value, 3);
    }

    [Fact]
    public void SessionsAccumulateAcrossTheGapsBetweenThem()
    {
        var tally = default(GatherTally);

        // An evening, a night away from the game, and another evening.
        for (var node = 0; node < 5; node++)
            tally = GatherPace.Add(tally, items: 20, secondsSinceLast: 60, Gap);

        tally = GatherPace.Add(tally, items: 20, secondsSinceLast: 40_000, Gap);

        for (var node = 0; node < 5; node++)
            tally = GatherPace.Add(tally, items: 20, secondsSinceLast: 60, Gap);

        Assert.Equal(200, tally.Items);
        Assert.Equal(600, tally.Seconds);
        Assert.Equal(1200, tally.PerHour!.Value, 3);
    }

    [Fact]
    public void TooLittleMeasuredIsNotWorthQuoting()
    {
        // A rate off ninety seconds of one lucky node is not a measurement, and offering it as
        // one would be worse than the placeholder it replaces.
        var tally = GatherPace.Add(default, items: 20, secondsSinceLast: 90, Gap);

        Assert.Null(tally.PerHour);
    }

    [Fact]
    public void GatheringNothingStillCountsTheTime()
    {
        // Failed attempts and walking to an empty node are part of an hour and part of the
        // rate.
        var tally = GatherPace.Add(default, items: 0, secondsSinceLast: 60, Gap);

        Assert.Equal(60, tally.Seconds);
    }
}

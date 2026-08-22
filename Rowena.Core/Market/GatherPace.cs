namespace Rowena.Core.Market;

/// <summary>How much gathering has been watched, and what came of it.</summary>
public readonly record struct GatherTally(int Items, double Seconds)
{
    /// <summary>
    /// Below this there is not enough watched to say anything.
    /// </summary>
    /// <remarks>
    /// Ten minutes. A rate off ninety seconds of one lucky node is not a measurement, and
    /// offering it as one would be worse than the assumption it replaces: an assumption
    /// announces itself, a bad measurement does not.
    /// </remarks>
    private const double Enough = 600d;

    /// <summary>Items an hour, or nothing when too little has been watched to say.</summary>
    public double? PerHour => Seconds >= Enough ? Items * 3600d / Seconds : null;
}

/// <summary>
/// How fast gathering actually goes, measured rather than assumed.
/// </summary>
/// <remarks>
/// The session planner rests on a number nobody had measured, and every gil figure it prints
/// is scaled by it. This is that number, taken from what actually happened.
///
/// The rate counts the time between gathers, not the time spent at a node. An hour of
/// gathering is mostly getting to the next one, and a rate measured only while standing at a
/// node is one nobody sustains for an hour. Long gaps are not counted at all: standing
/// somewhere for an hour and then gathering does not make the rate one an hour.
/// </remarks>
public static class GatherPace
{
    /// <summary>
    /// Folds one gather into the tally.
    /// </summary>
    /// <param name="secondsSinceLast">
    /// Since the previous gather. Longer than <paramref name="gapSeconds"/> means this is the
    /// start of a session rather than part of one, and starts the clock instead of counting.
    /// </param>
    public static GatherTally Add(GatherTally tally, int items, double secondsSinceLast, double gapSeconds) =>
        secondsSinceLast <= 0 || secondsSinceLast > gapSeconds
            ? tally
            : new GatherTally(tally.Items + Math.Max(0, items), tally.Seconds + secondsSinceLast);
}

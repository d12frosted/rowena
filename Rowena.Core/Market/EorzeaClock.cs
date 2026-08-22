namespace Rowena.Core.Market;

/// <summary>
/// The game's own clock, which runs about twenty-one times faster than ours.
/// </summary>
/// <remarks>
/// Worth having as its own thing because the conversion is the whole point. A node that is up
/// for four hours sounds like an afternoon and is under twelve minutes of anybody's evening;
/// one up for two hours is under six. Read in game hours, a timed node looks like something
/// you could get round to. Read in real minutes, it is something you either walk to now or
/// miss, and that is the difference between a plan you can act on and a list to cross-check
/// somewhere else.
/// </remarks>
public static class EorzeaClock
{
    /// <summary>A day there, in seconds here.</summary>
    private const int RealSecondsPerDay = 4200;

    /// <summary>Minutes in a day, theirs and ours alike.</summary>
    public const int MinutesPerDay = 1440;

    /// <summary>Where the game's clock stands, in minutes since its midnight.</summary>
    public static int MinuteOfDay(DateTimeOffset at) =>
        (int)(at.ToUnixTimeSeconds() * MinutesPerDay / RealSecondsPerDay % MinutesPerDay);

    /// <summary>How long a stretch of their time takes out of ours.</summary>
    public static TimeSpan ToReal(int eorzeaMinutes) =>
        TimeSpan.FromSeconds((double)eorzeaMinutes * RealSecondsPerDay / MinutesPerDay);
}

/// <summary>
/// A stretch of the game's day when a node is standing there.
/// </summary>
/// <remarks>
/// Kept as a start and a length rather than a start and an end, because the sheets give
/// lengths and because a window that runs past midnight is otherwise a pair of numbers in the
/// wrong order rather than a thing with a duration.
/// </remarks>
public readonly record struct EorzeaWindow(int StartMinute, int LengthMinutes)
{
    /// <summary>How far into the window a given moment is, wrapping at midnight.</summary>
    private int Into(int minuteOfDay) =>
        ((minuteOfDay - StartMinute) % EorzeaClock.MinutesPerDay + EorzeaClock.MinutesPerDay)
        % EorzeaClock.MinutesPerDay;

    public bool IsOpenAt(int minuteOfDay) => Into(minuteOfDay) < LengthMinutes;

    /// <summary>Game minutes until it opens, or nought when it already is.</summary>
    public int MinutesUntilOpen(int minuteOfDay) =>
        IsOpenAt(minuteOfDay) ? 0 : EorzeaClock.MinutesPerDay - Into(minuteOfDay);

    /// <summary>Game minutes left of it, or nought when it is not open.</summary>
    public int MinutesLeftAt(int minuteOfDay) =>
        IsOpenAt(minuteOfDay) ? LengthMinutes - Into(minuteOfDay) : 0;

    /// <summary>The soonest of several windows, or null when there are none.</summary>
    public static int? NextOpen(IReadOnlyList<EorzeaWindow> windows, int minuteOfDay) =>
        windows.Count == 0 ? null : windows.Min(window => window.MinutesUntilOpen(minuteOfDay));

    /// <summary>How long is left of whichever of them is open, or nought when none is.</summary>
    public static int LeftOf(IReadOnlyList<EorzeaWindow> windows, int minuteOfDay) =>
        windows.Count == 0 ? 0 : windows.Max(window => window.MinutesLeftAt(minuteOfDay));
}

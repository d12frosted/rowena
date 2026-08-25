namespace Rowena.Core.Market;

/// <summary>How much a held answer is worth right now.</summary>
public enum Standing
{
    /// <summary>Nobody has asked the board about this at all.</summary>
    Unknown,

    /// <summary>Fetched recently enough to price against.</summary>
    Fresh,

    /// <summary>Past its shelf life. The numbers beside it are last time's.</summary>
    Stale,
}

/// <summary>
/// How old a held price is, and whether that is old enough to matter.
/// </summary>
/// <remarks>
/// Three states rather than two, because "nothing is under you" and "nobody has looked yet"
/// are the same silence on a row and only one of them is a fact. A listing with no book at
/// all draws exactly like a listing that is correctly priced, and there is no way to tell
/// them apart from the row, which turns every reading into a trip to the board to check.
///
/// The shelf life is the caller's, not one number, for the same reason the cache's staleness
/// check takes one: a refetch and a row's verdict about age have to run on the same clock, or
/// the column says old about things that are about to be fetched and current about things
/// that are not.
/// </remarks>
/// <param name="Age">How long since it was fetched, or null when it never was.</param>
public readonly record struct Freshness(Standing Standing, TimeSpan? Age)
{
    public static Freshness Of(DateTimeOffset? fetched, DateTimeOffset now, TimeSpan shelfLife)
    {
        if (fetched is not { } at)
            return new Freshness(Standing.Unknown, null);

        // A clock that stepped back, or a machine that slept, leaves a snapshot dated ahead of
        // now. Whatever that is, it is not old, and a column has no way to draw minus four minutes.
        var age = now - at;

        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        return new Freshness(age > shelfLife ? Standing.Stale : Standing.Fresh, age);
    }
}

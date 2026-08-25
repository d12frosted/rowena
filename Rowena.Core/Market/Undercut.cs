namespace Rowena.Core.Market;

public enum UndercutWhy
{
    /// <summary>Somebody is listed below me and the board serves them first.</summary>
    Queue,

    /// <summary>Nobody is paying what I ask, cheapest on the board or not.</summary>
    NobodyPays,

    /// <summary>Nobody is under me and the next listing is far enough up that mine is money left behind.</summary>
    RoomAbove,
}

/// <summary>What repricing to where it would actually sell would mean.</summary>
/// <param name="Mine">What I am asking now, so the size of the move can be read off the plan.</param>
/// <param name="Target">The price to ask.</param>
/// <param name="Below">What the target sits under: the cheapest listing in front, what people pay, or the next listing up.</param>
/// <param name="UnitsAhead">How many units the board serves before mine.</param>
public readonly record struct UndercutPlan(
    long Mine,
    long Target,
    long Below,
    int UnitsAhead,
    UndercutWhy Why = UndercutWhy.Queue)
{
    /// <summary>
    /// What the move does to my price, per unit. Negative cuts it, positive raises it.
    /// </summary>
    /// <remarks>
    /// Carried because the two numbers a row naturally shows are the floor and the target, and
    /// side by side they describe the wrong move. A listing at 4,000 undercutting a floor of
    /// 2,000 reads as "2,000 -> 1,995", five gil, when what is actually being given up is
    /// 2,005 a unit. The plan knows both ends, so nothing downstream has to reconstruct one.
    /// </remarks>
    public long Move => Target - Mine;

    /// <summary>The move as a share of what I am asking, which is the part that reads as steep.</summary>
    public double Share => Mine <= 0 ? 0d : (double)Move / Mine;
}

/// <summary>
/// The reprice, priced.
/// </summary>
/// <remarks>
/// Deliberately dumb. Whether to reprice is the question <see cref="ListingDiagnosis"/>
/// exists to argue about; this only says what it would cost to do it, so the decision can be
/// taken with a number in hand rather than reopened every time a retainer is visited.
///
/// Three reasons to move. Somebody cheaper in front, which is the usual one; nobody paying
/// what I ask, which being cheapest on the board does not cure; and nobody anywhere near me,
/// which is the same mistake in the other direction. Measured: Mozzarella listed at
/// 389,994 on a board where every listing sits there and everything that trades goes for
/// under 1,500. A wall of listings nobody takes is not a market, and the front of it is not a
/// position, so the target there is what people pay, not what the wall asks.
///
/// Strictly below, because a listing at my own price is indistinguishable from mine from out
/// here, and counting it as a competitor would have me undercutting my own stock.
/// </remarks>
public static class Undercut
{
    /// <summary>How many recent sales it takes before "what people pay" is worth acting on.</summary>
    /// <remarks>A couple of fire-sale buys should not drag a legitimately dear item down to them.</remarks>
    public const int EnoughSales = 5;

    /// <param name="hq">The quality of my listing, which decides who counts as in front of it.</param>
    public static UndercutPlan? Of(long mine, OrderBook? book, long margin, bool hq = false)
    {
        if (book is null || mine <= 0)
            return null;

        var ahead = book.Listings.Where(listing => listing.UnitPrice < mine && listing.Serves(hq)).ToArray();
        var units = ahead.Sum(listing => listing.Quantity);
        long? below = ahead.Length == 0 ? null : ahead.Min(listing => listing.UnitPrice);
        var paid = Median(book.RecentSales);

        // What people pay, if enough of them have, and whether the cheapest thing on the board
        // (mine, or whoever is in front) is far above it. The factor is the diagnosis's own, so
        // the row that says "nobody pays this" and the button that fixes it agree.
        if (book.RecentSales.Count >= EnoughSales && paid > 0 && (below ?? mine) > paid * ListingDiagnosis.Rich)
            return new UndercutPlan(mine, Under(paid, margin), paid, units, UndercutWhy.NobodyPays);

        if (below is { } floor)
            return new UndercutPlan(mine, Under(floor, margin), floor, units);

        // Cheapest, with more room above than being cheapest requires. The bars are the
        // diagnosis's again, so the row that says "you could ask more" and the button agree:
        // the gap has to be worth the bother, and recent sales have to say somebody actually
        // pays up there, or the room is somebody else's fantasy. Measured against the next
        // listing of any quality, because raising past a cheaper NQ hands every buyer who
        // does not care about quality a better deal than mine.
        var next = book.Listings.FirstOrDefault(listing => listing.UnitPrice > mine).UnitPrice;

        return next > 0
               && next - 1 >= mine * ListingDiagnosis.Worthwhile
               && paid >= (next - 1) * ListingDiagnosis.Supported
               && Under(next, margin) > mine
            ? new UndercutPlan(mine, Under(next, margin), next, units, UndercutWhy.RoomAbove)
            : null;
    }

    private static long Under(long price, long margin) => Math.Max(1, price - Math.Max(0, margin));

    private static long Median(IReadOnlyList<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }
}

namespace Rowena.Core.Market;

public enum UndercutWhy
{
    /// <summary>Somebody is listed below me and the board serves them first.</summary>
    Queue,

    /// <summary>Nobody is paying what I ask, cheapest on the board or not.</summary>
    NobodyPays,
}

/// <summary>What repricing to where it would actually sell would mean.</summary>
/// <param name="Target">The price to ask.</param>
/// <param name="Below">What the target sits under: the cheapest listing in front, or what people pay.</param>
/// <param name="UnitsAhead">How many units the board serves before mine.</param>
public readonly record struct UndercutPlan(long Target, long Below, int UnitsAhead, UndercutWhy Why = UndercutWhy.Queue);

/// <summary>
/// The reprice, priced.
/// </summary>
/// <remarks>
/// Deliberately dumb. Whether to reprice is the question <see cref="ListingDiagnosis"/>
/// exists to argue about; this only says what it would cost to do it, so the decision can be
/// taken with a number in hand rather than reopened every time a retainer is visited.
///
/// Two reasons to move. Somebody cheaper in front, which is the usual one; and nobody paying
/// what I ask, which being cheapest on the board does not cure. Measured: Mozzarella listed at
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

    public static UndercutPlan? Of(long mine, OrderBook? book, long margin)
    {
        if (book is null || mine <= 0)
            return null;

        var ahead = book.Listings.Where(listing => listing.UnitPrice < mine).ToArray();
        var units = ahead.Sum(listing => listing.Quantity);
        long? below = ahead.Length == 0 ? null : ahead.Min(listing => listing.UnitPrice);

        // What people pay, if enough of them have, and whether the cheapest thing on the board
        // (mine, or whoever is in front) is far above it. The factor is the diagnosis's own, so
        // the row that says "nobody pays this" and the button that fixes it agree.
        if (book.RecentSales.Count >= EnoughSales
            && Median(book.RecentSales) is var paid and > 0
            && (below ?? mine) > paid * ListingDiagnosis.Rich)
        {
            return new UndercutPlan(Under(paid, margin), paid, units, UndercutWhy.NobodyPays);
        }

        return below is { } floor
            ? new UndercutPlan(Under(floor, margin), floor, units)
            : null;
    }

    private static long Under(long price, long margin) => Math.Max(1, price - Math.Max(0, margin));

    private static long Median(IReadOnlyList<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }
}

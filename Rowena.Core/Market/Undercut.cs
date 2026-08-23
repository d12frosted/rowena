namespace Rowena.Core.Market;

/// <summary>What relisting under everyone in front of me would mean.</summary>
/// <param name="Target">The price to ask to be cheapest on the board.</param>
/// <param name="Below">The cheapest listing currently in front of me.</param>
/// <param name="UnitsAhead">How many units the board serves before mine.</param>
public readonly record struct UndercutPlan(long Target, long Below, int UnitsAhead);

/// <summary>
/// The undercut, priced.
/// </summary>
/// <remarks>
/// Deliberately dumb. Whether to undercut is the question <see cref="ListingDiagnosis"/>
/// exists to argue about; this only says what it would cost to do it, so the decision can be
/// taken with a number in hand rather than reopened every time a retainer is visited.
///
/// Strictly below, because a listing at my own price is indistinguishable from mine from out
/// here, and counting it as a competitor would have me undercutting my own stock.
/// </remarks>
public static class Undercut
{
    public static UndercutPlan? Of(long mine, OrderBook? book, long margin)
    {
        if (book is null || mine <= 0)
            return null;

        var ahead = book.Listings.Where(listing => listing.UnitPrice < mine).ToArray();

        if (ahead.Length == 0)
            return null;

        var below = ahead.Min(listing => listing.UnitPrice);

        return new UndercutPlan(
            Math.Max(1, below - Math.Max(0, margin)),
            below,
            ahead.Sum(listing => listing.Quantity));
    }
}

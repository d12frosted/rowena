namespace Rowena.Core.Market;

/// <summary>What to do about a listing of mine, if anything.</summary>
public enum ListingCall
{
    /// <summary>At or near the front of the queue. Nothing to do.</summary>
    Hold,

    /// <summary>Behind a queue, but one the board will eat through soon enough.</summary>
    Wait,

    /// <summary>Behind more than the board will get through while you care.</summary>
    Chase,

    /// <summary>The vendor pays more than the board would leave you with.</summary>
    Vendor,

    /// <summary>Priced above anything this has recently sold for.</summary>
    Overpriced,

    /// <summary>Cheapest on the board, and well under what people have been paying.</summary>
    Underpriced,

    /// <summary>Nothing sells here at any price.</summary>
    Stuck,
}

/// <summary>
/// What one of my listings is actually doing, and what that suggests.
/// </summary>
/// <remarks>
/// A diagnosis rather than an undercut button, deliberately. Undercutting is the one move
/// the board makes easy and it is usually the wrong one: the question is never "is somebody
/// cheaper than me" but "how long until the board has eaten through everyone cheaper than
/// me", and those have very different answers. Three units ahead on a board that sells ten a
/// day are gone this afternoon, and dropping your price to jump them is a haircut for
/// nothing. Two hundred ahead is a different conversation.
///
/// That is the same reading of depth the rest of this library is built on, pointed at the
/// listings you already have rather than the ones you might buy. Every number behind the
/// verdict is kept so the window can show its working: a call you cannot check is not worth
/// more than the guess it replaced.
/// </remarks>
/// <param name="UnitsAhead">Units listed below mine, which the board serves first.</param>
/// <param name="DaysToClear">How long until the last of mine goes, or null when nothing sells.</param>
/// <param name="NetHolding">What I keep per unit if it sells at my price.</param>
/// <param name="NetChasing">What I would keep per unit at the current floor.</param>
/// <param name="TypicalSale">What it has actually been changing hands for, when it has.</param>
/// <param name="CouldAsk">The most I could ask and still be the cheapest, when I am.</param>
/// <param name="VendorNet">What the vendor pays, when there is one.</param>
public readonly record struct ListingDiagnosis(
    ListingCall Call,
    int UnitsAhead,
    double? DaysToClear,
    long Floor,
    long Mine,
    long NetHolding,
    long NetChasing,
    long? TypicalSale,
    long? CouldAsk,
    long? VendorNet)
{
    /// <summary>How far above recent sales a price has to be before it is worth mentioning.</summary>
    private const double Rich = 1.5d;

    /// <summary>How much room above has to be there before raising is worth the bother.</summary>
    private const double Worthwhile = 1.1d;

    /// <summary>How far recent sales may sit below that room before it is somebody else's fantasy.</summary>
    private const double Supported = 0.9d;

    /// <summary>
    /// Reads one listing against the board it is sitting on.
    /// </summary>
    /// <param name="patienceDays">
    /// How long you are willing to be selling. The same horizon the rest of the plugin plans
    /// over: past it you are holding stock rather than earning.
    /// </param>
    public static ListingDiagnosis? Of(
        long mine,
        int units,
        OrderBook? book,
        long vendorPrice,
        MarketTax tax,
        int patienceDays)
    {
        if (book is null || mine <= 0)
            return null;

        // Strictly below, because a tie is ambiguous from out here: the board does not say
        // which of the listings at my price is mine, and counting them as ahead of me would
        // manufacture a queue out of my own stock.
        var ahead = book.Listings.Where(listing => listing.UnitPrice < mine).Sum(listing => listing.Quantity);

        var days = book.SaleVelocityPerDay > 0 ? (ahead + units) / book.SaleVelocityPerDay : (double?)null;
        var floor = book.CredibleFloor() ?? mine;

        var reading = new ListingDiagnosis(
            ListingCall.Hold,
            ahead,
            days,
            floor,
            mine,
            tax.NetProceeds(mine),
            tax.NetProceeds(floor),
            book.RecentSales.Count > 0 ? Median(book.RecentSales) : null,
            ahead == 0 ? Headroom(book, mine) : null,
            vendorPrice > 0 ? vendorPrice : null);

        return reading with { Call = Verdict(reading, book, days, patienceDays) };
    }

    /// <summary>
    /// The one line of advice, from the numbers already worked out.
    /// </summary>
    /// <remarks>
    /// Ordered by how much each finding overrules the next. A vendor beating your own asking
    /// price ends the discussion, since no amount of patience on the board catches up with a
    /// buyer who is already paying more. A board where nothing sells cannot be waited out or
    /// chased down, so the queue is beside the point. Only then is the queue worth reading, and
    /// only with no queue at all does being cheap become a finding rather than a position.
    /// </remarks>
    private static ListingCall Verdict(ListingDiagnosis reading, OrderBook book, double? days, int patienceDays)
    {
        if (reading.VendorNet is { } vendor && vendor >= reading.NetHolding)
            return ListingCall.Vendor;

        if (days is null)
            return ListingCall.Stuck;

        if (reading.TypicalSale is { } usual && reading.Mine > usual * Rich)
            return ListingCall.Overpriced;

        if (reading.UnitsAhead > 0)
            return days <= patienceDays ? ListingCall.Wait : ListingCall.Chase;

        // Cheapest on the board with real room above. Measured against the next listing rather
        // than against past sales: a nugget at 895 with the next at 900 reads as a third under
        // the going rate if you go by history, and raising it earns four gil. The gap to the
        // listing above is the money actually on the table, and recent sales only have to say
        // somebody is paying up there.
        return reading is { CouldAsk: { } target, TypicalSale: { } paid }
               && target >= reading.Mine * Worthwhile
               && paid >= target * Supported
            ? ListingCall.Underpriced
            : ListingCall.Hold;
    }

    /// <summary>The most I could ask and still be the cheapest thing on the board.</summary>
    private static long? Headroom(OrderBook book, long mine) =>
        book.Listings.FirstOrDefault(listing => listing.UnitPrice > mine) is { UnitPrice: > 0 } next
            ? next.UnitPrice - 1
            : null;

    private static long Median(IReadOnlyList<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    /// <summary>What chasing the floor costs per unit, which is the number worth seeing first.</summary>
    public long Haircut => Math.Max(0, NetHolding - NetChasing);
}

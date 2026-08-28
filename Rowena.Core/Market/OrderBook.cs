namespace Rowena.Core.Market;

/// <summary>A price point in the book, with everything at or below it accumulated.</summary>
/// <remarks>
/// This is the shape a display wants: "13 units at or under 49,000, 28 more by 50,000"
/// says more about whether a buy is possible than any single average does.
/// </remarks>
public readonly record struct DepthTier(long UnitPrice, int CumulativeUnits, long CumulativeTotal);

/// <summary>
/// One item's listings on one world or data centre, cheapest first.
/// </summary>
/// <remarks>
/// Everything in this class exists because the cheapest listing is a bad summary of a
/// market. A floor of 48,795 with three units behind it and a floor of 48,795 with three
/// hundred are completely different propositions, and only the second one supports a
/// trade of any size. Sale velocity is carried alongside because a book you can clear is
/// still useless if the item only moves twice a day.
/// </remarks>
public sealed class OrderBook
{
    private OrderBook(
        uint itemId,
        IReadOnlyList<Listing> listings,
        double saleVelocityPerDay,
        DateTimeOffset retrieved,
        bool complete,
        MarketSource source,
        IReadOnlyList<Sale> recentSales)
    {
        ItemId = itemId;
        Listings = listings;
        SaleVelocityPerDay = saleVelocityPerDay;
        Retrieved = retrieved;
        Complete = complete;
        Source = source;
        RecentSales = recentSales;
    }

    public uint ItemId { get; }

    /// <summary>Listings sorted ascending by unit price.</summary>
    public IReadOnlyList<Listing> Listings { get; }

    /// <summary>Units sold per day, as reported by the source.</summary>
    public double SaleVelocityPerDay { get; }

    /// <summary>
    /// Whether that rate is known at all.
    /// </summary>
    /// <remarks>
    /// Nought means two different things and they must not read the same: nothing sells here,
    /// or nobody has asked yet. A listings response carries no rate this library can use, since
    /// that endpoint counts sales where everything here counts units, so a book arrives without
    /// one and the summary supplies it afterwards. Reporting the gap as "never sells" would put
    /// the worst possible verdict on every item the moment it was first looked at.
    /// </remarks>
    public bool RateKnown { get; private init; } = true;

    /// <summary>When this snapshot was taken. Market data goes stale quickly.</summary>
    public DateTimeOffset Retrieved { get; }

    /// <summary>
    /// Whether these are all the listings there are.
    /// </summary>
    /// <remarks>
    /// A fetch asks for at most so many listings, and one that comes back holding exactly that
    /// many has probably been cut off: the response counts its own contents, so it cannot say.
    /// What is cut off is the dear end, which means everything here is priced correctly and
    /// only running past the end is unknown. Saying "the board is short" when it is merely out
    /// of view understates what can be done, which is the opposite of the mistake this library
    /// usually guards against and just as wrong.
    /// </remarks>
    public bool Complete { get; }

    /// <summary>Where these listings came from.</summary>
    public MarketSource Source { get; }

    /// <summary>What the item actually changed hands for lately, newest first.</summary>
    public IReadOnlyList<Sale> RecentSales { get; }

    /// <summary>How many recent sales it takes before "what people pay" is worth acting on.</summary>
    /// <remarks>A couple of fire-sale buys should not drag a legitimately dear item down to them.</remarks>
    public const int EnoughSales = 5;

    /// <summary>How far back a sale still describes the market rather than its history.</summary>
    public static readonly TimeSpan Lately = TimeSpan.FromDays(7);

    /// <summary>
    /// What people pay, weighted towards the people paying it now.
    /// </summary>
    /// <remarks>
    /// The median of the last week's sales, when a week is enough of them to speak; the
    /// median of everything held, when it is not. Measured, on a parasol asking 340,989:
    /// twenty sales split into an old cluster at 126,000 and a last-week cluster at 300,000,
    /// and the time-blind median quoted the old one, calling for a reprice to half of what
    /// the five most recent buyers had just paid. A median is robust against a fire sale;
    /// only the window makes it honest about a market that moved.
    ///
    /// Ages are measured against <see cref="Retrieved"/> rather than the reader's clock, so
    /// a book answers the same way for as long as it is held: its sales do not slide out of
    /// the window while the book itself goes stale, which is <see cref="Freshness"/>'s to say.
    /// </remarks>
    public long? TypicalSale
    {
        get
        {
            if (RecentSales.Count == 0)
                return null;

            // Guarded subtraction: a book that never said when it was retrieved sits at the
            // epoch, and a week before the beginning of time is not a DateTimeOffset.
            var cutoff = Retrieved - DateTimeOffset.MinValue <= Lately
                ? DateTimeOffset.MinValue
                : Retrieved - Lately;

            var lately = RecentSales
                .Where(sale => sale.At >= cutoff)
                .Select(sale => sale.UnitPrice)
                .ToArray();

            return Median(lately.Length >= EnoughSales ? lately : [.. RecentSales.Select(sale => sale.UnitPrice)]);
        }
    }

    /// <summary>
    /// The floor, when anybody could plausibly be paying it.
    /// </summary>
    /// <remarks>
    /// A book holding one listing at 999,999,999 gil is not a market, it is somebody parking an
    /// item, and the floor of it is not a price. Measured: a Hanya Mask listed at a billion,
    /// against recent sales between 120,000 and 450,000, made the craft ranking report eight
    /// hundred million gil a day and put it top of the table.
    ///
    /// Recent sales are the evidence, since they are what somebody actually paid. A floor far
    /// above all of them is not evidence of anything, and is worth refusing rather than
    /// quoting: this library exists to say the floor is a bad summary of a market, and a
    /// fantasy floor is that failure at its worst.
    ///
    /// Only ever refuses. A floor below recent sales is an ordinary bargain, not a mistake, and
    /// a book with no history to judge against is left alone.
    /// </remarks>
    public long? CredibleFloor(double factor = 5d)
    {
        if (Floor is not { } floor)
            return null;

        return TypicalSale is > 0 and var typical && floor > typical * factor ? null : floor;
    }

    private static long Median(IReadOnlyList<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    /// <summary>
    /// Builds a book, sorting defensively. Universalis happens to return listings in
    /// price order, but nothing in the arithmetic below should depend on a remote
    /// service's incidental behaviour.
    /// </summary>
    public static OrderBook Create(
        uint itemId,
        IEnumerable<Listing> listings,
        double saleVelocityPerDay = 0d,
        DateTimeOffset retrieved = default,
        bool complete = true,
        MarketSource source = MarketSource.Universalis,
        IReadOnlyList<Sale>? recentSales = null)
    {
        var sorted = listings.OrderBy(listing => listing.UnitPrice).ToArray();
        return new OrderBook(itemId, sorted, saleVelocityPerDay, retrieved, complete, source, recentSales ?? []);
    }

    /// <summary>An empty book, which is different from an absent one: nothing is for sale.</summary>
    public static OrderBook Empty(uint itemId) => Create(itemId, []);

    /// <summary>
    /// The same listings, with the sale rate replaced.
    /// </summary>
    /// <remarks>
    /// Universalis reports two different sale velocities, one alongside the listings and one from
    /// its summary endpoint, and they disagree: for one furnishing they differ by more than three
    /// times. Whichever is right, using both would mean shortlisting an item on one number and
    /// ranking it on another. This exists so a single source can be imposed on everything.
    /// </remarks>
    public OrderBook WithVelocity(double saleVelocityPerDay) =>
        new(ItemId, Listings, saleVelocityPerDay, Retrieved, Complete, Source, RecentSales) { RateKnown = true };

    /// <summary>The same listings, with no idea how fast they move.</summary>
    public OrderBook WithoutRate() =>
        new(ItemId, Listings, 0d, Retrieved, Complete, Source, RecentSales) { RateKnown = false };

    /// <summary>The same listings, said to be all of them or not.</summary>
    public OrderBook WithCompleteness(bool complete) =>
        new(ItemId, Listings, SaleVelocityPerDay, Retrieved, complete, Source, RecentSales)
        {
            RateKnown = RateKnown,
        };

    /// <summary>Total units listed.</summary>
    public int UnitsListed => Listings.Sum(listing => listing.Quantity);

    /// <summary>The cheapest unit price, or null when nothing is listed.</summary>
    public long? Floor => Listings.Count == 0 ? null : Listings[0].UnitPrice;

    /// <summary>
    /// Walks the book from the cheapest listing up, consuming whole or partial listings
    /// until the order is filled or the board runs dry.
    /// </summary>
    /// <remarks>
    /// The buyer's tax is charged per listing and floored per listing, which is how the
    /// board does it: a recorded listing of 146,385 carries exactly 7,319. A partial take
    /// is taxed on what was spent on that listing, the consistent extension of a model
    /// that already allows partial fills.
    /// </remarks>
    public BuyQuote CostToBuy(int quantity, MarketTax tax)
    {
        if (quantity <= 0)
            return new BuyQuote(Math.Max(0, quantity), 0, 0, 0, 0);

        long total = 0;
        long taxed = 0;
        var filled = 0;
        long worst = 0;

        foreach (var listing in Listings)
        {
            if (filled >= quantity)
                break;

            var taken = Math.Min(listing.Quantity, quantity - filled);
            var spent = listing.UnitPrice * taken;
            total += spent;
            taxed += tax.OnPurchase(spent);
            filled += taken;
            worst = listing.UnitPrice;
        }

        // Running past the end of a book that was cut off is not a shortfall: the listings we
        // cannot see are the dear ones, and they may well cover the rest.
        return new BuyQuote(quantity, filled, total + taxed, worst, taxed, !Complete && filled < quantity);
    }

    /// <summary>
    /// The book with the cheapest <paramref name="units"/> taken off it.
    /// </summary>
    /// <remarks>
    /// For working out what a second buyer faces once a first one has been served, which is
    /// the whole of <see cref="Conversions.ConversionAllocation"/>. Partially consumed
    /// listings survive with the remainder of their quantity, since a listing of ten with
    /// three taken is a listing of seven and not nothing.
    /// </remarks>
    public OrderBook WithoutCheapest(int units)
    {
        if (units <= 0)
            return this;

        var remaining = new List<Listing>();
        var toDrop = units;

        foreach (var listing in Listings)
        {
            if (toDrop <= 0)
            {
                remaining.Add(listing);
                continue;
            }

            if (listing.Quantity <= toDrop)
            {
                toDrop -= listing.Quantity;
                continue;
            }

            remaining.Add(listing with { Quantity = listing.Quantity - toDrop });
            toDrop = 0;
        }

        return Create(ItemId, remaining, SaleVelocityPerDay, Retrieved, Complete, Source, RecentSales);
    }

    /// <summary>
    /// How many units can be had without paying more than <paramref name="unitPrice"/>
    /// for any one of them. The answer to "how much of this is cheap right now".
    /// </summary>
    public int UnitsAtOrBelow(long unitPrice) =>
        Listings.Where(listing => listing.UnitPrice <= unitPrice).Sum(listing => listing.Quantity);

    /// <summary>
    /// The book collapsed to one entry per distinct price, with running totals.
    /// </summary>
    public IReadOnlyList<DepthTier> Tiers()
    {
        var tiers = new List<DepthTier>();
        var units = 0;
        long total = 0;

        foreach (var group in Listings.GroupBy(listing => listing.UnitPrice).OrderBy(group => group.Key))
        {
            units += group.Sum(listing => listing.Quantity);
            total += group.Sum(listing => listing.Total);
            tiers.Add(new DepthTier(group.Key, units, total));
        }

        return tiers;
    }

    /// <summary>
    /// Roughly how long the market would take to absorb <paramref name="units"/> at the
    /// observed rate, ignoring everyone else selling into it. Null when nothing sells,
    /// which means the honest answer is "never", not "instantly".
    /// </summary>
    public double? DaysToAbsorb(int units) =>
        SaleVelocityPerDay <= 0 ? null : units / SaleVelocityPerDay;
}

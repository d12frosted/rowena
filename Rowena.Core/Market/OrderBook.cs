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
        MarketSource source)
    {
        ItemId = itemId;
        Listings = listings;
        SaleVelocityPerDay = saleVelocityPerDay;
        Retrieved = retrieved;
        Complete = complete;
        Source = source;
    }

    public uint ItemId { get; }

    /// <summary>Listings sorted ascending by unit price.</summary>
    public IReadOnlyList<Listing> Listings { get; }

    /// <summary>Units sold per day, as reported by the source.</summary>
    public double SaleVelocityPerDay { get; }

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
        MarketSource source = MarketSource.Universalis)
    {
        var sorted = listings.OrderBy(listing => listing.UnitPrice).ToArray();
        return new OrderBook(itemId, sorted, saleVelocityPerDay, retrieved, complete, source);
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
        new(ItemId, Listings, saleVelocityPerDay, Retrieved, Complete, Source);

    /// <summary>The same listings, said to be all of them or not.</summary>
    public OrderBook WithCompleteness(bool complete) =>
        new(ItemId, Listings, SaleVelocityPerDay, Retrieved, complete, Source);

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
            taxed += tax.On(spent);
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

        return Create(ItemId, remaining, SaleVelocityPerDay, Retrieved, Complete, Source);
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

namespace Rowena.Core.Market;

/// <summary>
/// Listings priced under what a vendor pays: gil lying on the board.
/// </summary>
/// <remarks>
/// Rare, and real. Somebody lists a stack under the vendor price by mistake or to clear a
/// slot, and buying it to walk to any vendor is profit with no market risk at all. The
/// buyer's tax is what makes it a calculation rather than a glance: a listing a few gil
/// under the vendor price loses money once the board's cut is added.
/// </remarks>
public static class VendorArbitrage
{
    /// <param name="Units">How many can be bought at a gain.</param>
    /// <param name="Profit">What vendoring them pays over what they cost, tax included.</param>
    public readonly record struct Found(int Units, long Profit);

    /// <summary>
    /// Whether a book whose cheapest unit costs <paramref name="minListing"/> could hold any
    /// arbitrage at all, for deciding what is worth fetching in full.
    /// </summary>
    /// <remarks>
    /// A summary carries the cheapest price and nothing else, which is exactly enough: no
    /// listing in a book is cheaper than its floor, so a floor that already loses money loses
    /// more at every other price. The tax here is charged per unit, where the board charges it
    /// per listing and floors it, which understates the cost slightly and so lets a few
    /// hopeless books through. That is the safe direction for a filter: it never discards a
    /// book that would have paid.
    /// </remarks>
    public static bool Possible(long minListing, long vendorPrice, MarketTax tax) =>
        vendorPrice > 0 && minListing > 0 && minListing + tax.OnPurchase(minListing) < vendorPrice;

    /// <summary>Walks the book from the bottom, taking whole listings while they still pay.</summary>
    public static Found Find(OrderBook book, long vendorPrice, MarketTax tax)
    {
        if (vendorPrice <= 0)
            return new Found(0, 0);

        var units = 0;
        long profit = 0;

        foreach (var listing in book.Listings)
        {
            var cost = listing.Total + tax.OnPurchase(listing.Total);
            var gain = vendorPrice * listing.Quantity - cost;

            // Listings are sorted, so the first that loses means the rest lose more.
            if (gain <= 0)
                break;

            units += listing.Quantity;
            profit += gain;
        }

        return new Found(units, profit);
    }
}

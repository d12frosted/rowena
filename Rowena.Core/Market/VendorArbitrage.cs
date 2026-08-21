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

    /// <summary>Walks the book from the bottom, taking whole listings while they still pay.</summary>
    public static Found Find(OrderBook book, long vendorPrice, MarketTax tax)
    {
        if (vendorPrice <= 0)
            return new Found(0, 0);

        var units = 0;
        long profit = 0;

        foreach (var listing in book.Listings)
        {
            var cost = listing.Total + tax.On(listing.Total);
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

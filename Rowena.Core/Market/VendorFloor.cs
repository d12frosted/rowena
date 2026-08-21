namespace Rowena.Core.Market;

/// <summary>
/// What a vendor pays for an item, as the floor under every sale.
/// </summary>
/// <remarks>
/// Every tradable item has a vendor price, and it is the one buyer who never undercuts,
/// never takes a cut and never runs out of appetite. Below it, the market board is the
/// wrong counter: a board that nets less than a vendor after tax is a worse trade with
/// extra steps. Above it, the board wins and the vendor is irrelevant. So an output is
/// worth whichever pays more, and one sold to a vendor is sold the moment it is made.
/// </remarks>
public static class VendorFloor
{
    /// <summary>How a quantity of an item is best sold, given the board and the vendor.</summary>
    /// <param name="Gross">The sale before any cut.</param>
    /// <param name="Net">What is kept.</param>
    /// <param name="ToVendor">True when the vendor is the better counter.</param>
    public readonly record struct Sale(long Gross, long Net, bool ToVendor);

    /// <summary>
    /// Values a sale, or null when there is no way to price it.
    /// </summary>
    /// <remarks>
    /// A null book is no answer yet, not a bad one, and a vendor price must not paper over
    /// it: the row would read as a confident loss until the fetch arrived. An empty book is
    /// an answer, nobody lists it, and the vendor still buys it.
    /// </remarks>
    public static Sale? Value(OrderBook? book, long vendorPrice, int quantity, MarketTax tax)
    {
        if (book is null)
            return null;

        var vendor = vendorPrice > 0 ? vendorPrice * quantity : (long?)null;

        if (book.Floor is not { } floor)
            return vendor is { } only ? new Sale(only, only, true) : null;

        var gross = floor * quantity;
        var net = tax.NetProceeds(gross);

        return vendor is { } paid && paid >= net
            ? new Sale(paid, paid, true)
            : new Sale(gross, net, false);
    }

    /// <summary>Whether the vendor is the better counter for one unit of this book.</summary>
    public static bool Beats(OrderBook? book, long vendorPrice, MarketTax tax) =>
        Value(book, vendorPrice, 1, tax) is { ToVendor: true };
}

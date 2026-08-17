namespace Splendors.Core.Market;

/// <summary>One retainer's offer: a unit price, and how many units are behind it.</summary>
/// <remarks>
/// Quantity is the whole point. A book quoted only by its cheapest price hides
/// whether that price covers one unit or a hundred, and that difference is what
/// decides whether a trade is worth taking.
/// </remarks>
public readonly record struct Listing(long UnitPrice, int Quantity, string World)
{
    /// <summary>Gil to clear this listing entirely.</summary>
    public long Total => UnitPrice * Quantity;
}

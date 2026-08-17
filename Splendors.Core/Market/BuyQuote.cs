namespace Splendors.Core.Market;

/// <summary>What it would actually cost to take a given number of units off the board.</summary>
/// <param name="Requested">Units asked for.</param>
/// <param name="Filled">
/// Units the book can supply. Less than <paramref name="Requested"/> when the board
/// runs out, which is a real and common answer: wanting a hundred of something does
/// not mean a hundred exist.
/// </param>
/// <param name="Total">Gil for the filled units.</param>
/// <param name="WorstUnitPrice">
/// The price of the last listing consumed. This is the number that stings: it is what
/// the top of the order actually pays, as opposed to the floor the board advertises.
/// </param>
public readonly record struct BuyQuote(int Requested, int Filled, long Total, long WorstUnitPrice)
{
    /// <summary>True when the board could supply everything asked for.</summary>
    public bool IsComplete => Filled >= Requested;

    /// <summary>Units that could not be sourced.</summary>
    public int ShortBy => Math.Max(0, Requested - Filled);

    /// <summary>
    /// Blended price per unit across the whole purchase. Compare against the floor to
    /// see how much the depth of the book costs you.
    /// </summary>
    public double AverageUnitPrice => Filled == 0 ? 0d : (double)Total / Filled;

    /// <summary>
    /// How much more the purchase costs than the naive floor-times-quantity estimate
    /// that every market tool shows. Zero when one listing covers the whole order.
    /// </summary>
    public long PremiumOverFloor(long floor) => Total - floor * Filled;
}

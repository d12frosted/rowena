namespace Rowena.Core.Market;

/// <summary>What it would actually cost to take a given number of units off the board.</summary>
/// <param name="Requested">Units asked for.</param>
/// <param name="Filled">
/// Units the book can supply. Less than <paramref name="Requested"/> when the board
/// runs out, which is a real and common answer: wanting a hundred of something does
/// not mean a hundred exist.
/// </param>
/// <param name="Total">
/// Gil out of the buyer's pocket for the filled units, the board's cut included. The
/// sticker alone is not a price anyone can buy at, so it is not the number this carries;
/// subtract <paramref name="Tax"/> to see it.
/// </param>
/// <param name="WorstUnitPrice">
/// The price of the last listing consumed. This is the number that stings: it is what
/// the top of the order actually pays, as opposed to the floor the board advertises.
/// </param>
/// <param name="Tax">The part of <paramref name="Total"/> that was the board's cut.</param>
/// <param name="Uncertain">
/// True when the order ran past the end of a book that was cut off, so the shortfall is what
/// could not be seen rather than what is not there.
/// </param>
public readonly record struct BuyQuote(
    int Requested,
    int Filled,
    long Total,
    long WorstUnitPrice,
    long Tax,
    bool Uncertain = false)
{
    /// <summary>True when the board could supply everything asked for.</summary>
    public bool IsComplete => Filled >= Requested;

    /// <summary>Units that could not be sourced.</summary>
    public int ShortBy => Math.Max(0, Requested - Filled);

    /// <summary>
    /// Blended cost per unit across the whole purchase, tax included. Compare against
    /// the floor to see how much the depth of the book costs you.
    /// </summary>
    public double AverageUnitPrice => Filled == 0 ? 0d : (double)Total / Filled;

    /// <summary>
    /// How much more the purchase costs than the naive floor-times-quantity estimate
    /// that every market tool shows. Depth and tax both live in the gap, since the
    /// naive estimate includes neither.
    /// </summary>
    public long PremiumOverFloor(long floor) => Total - floor * Filled;
}

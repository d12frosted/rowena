namespace Rowena.Core.Market;

/// <summary>One retainer's offer: a unit price, and how many units are behind it.</summary>
/// <remarks>
/// Quantity is the whole point. A book quoted only by its cheapest price hides
/// whether that price covers one unit or a hundred, and that difference is what
/// decides whether a trade is worth taking.
///
/// Quality matters to one question: who is in front of whom. Somebody wanting NQ takes a
/// cheaper HQ happily; somebody wanting HQ does not take the NQ instead. So an HQ listing is
/// ahead of anything dearer, and an NQ listing is only ahead of dearer NQ.
/// </remarks>
public readonly record struct Listing(long UnitPrice, int Quantity, string World, bool IsHq = false)
{
    /// <summary>Whether a buyer of the given quality would take this before a dearer one of theirs.</summary>
    public bool Serves(bool hq) => IsHq || !hq;

    /// <summary>Gil to clear this listing entirely.</summary>
    public long Total => UnitPrice * Quantity;
}

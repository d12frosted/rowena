namespace Rowena.Core.Market;

/// <summary>What I sold of one thing, against what its market moved in the same time.</summary>
/// <param name="MarketPerDay">Units a day the whole board turns over.</param>
/// <param name="Days">How long the window is.</param>
public readonly record struct Captured(uint ItemId, int MySold, double MarketPerDay, double Days);

/// <summary>
/// What share of a market I actually take.
/// </summary>
/// <remarks>
/// Every ranking here is in gil a day, and every one of them says out loud that the figure is
/// a ceiling: it assumes taking every sale at today's price. That is honest and it is not a
/// forecast, and the gap between the two has never been measured.
///
/// It can be. What the board turns over is known and what I sold is now recorded, so the ratio
/// is a measurement rather than a fudge factor. Multiplying a ceiling by it turns it into
/// something that can be believed.
///
/// Weighted by what each market moved rather than averaged across items, because the mean of
/// two fractions says a busy market I barely touch counts as much as a quiet one I own. The
/// question is what share of everything that moved was mine.
///
/// It refuses to answer at all unless most of what I sold has a rate to weigh against. The
/// items whose rate is missing are not a random sample: whatever is left is skewed towards the
/// small quiet markets a person takes most of, so measured off a third of the sales it came
/// out at eleven percent where the full set said four.
/// </remarks>
public static class CaptureRate
{
    /// <summary>
    /// The share of the market my sales came to, or nothing when too little has been seen.
    /// </summary>
    /// <param name="minSales">
    /// How many sales are wanted before quoting a share. A ratio off three of them is not a
    /// measurement, and offering it as one would be worse than the ceiling it replaces: a
    /// ceiling announces itself, a bad measurement does not.
    /// </param>
    /// <param name="minCoverage">
    /// How much of what I sold must have a rate to weigh it against. Below this the answer is
    /// drawn from whichever items happen to have been priced, which is not a sample of anything.
    /// </param>
    public static double? Of(IEnumerable<Captured> observations, int minSales, double minCoverage = 0.7d)
    {
        var all = observations.Where(seen => seen is { MySold: > 0, Days: > 0 }).ToArray();
        var usable = all.Where(seen => seen.MarketPerDay > 0).ToArray();

        if (usable.Length == 0)
            return null;

        var sold = all.Sum(seen => (double)seen.MySold);
        var mine = usable.Sum(seen => (double)seen.MySold);

        if (mine < minSales || sold <= 0 || mine / sold < minCoverage)
            return null;

        var moved = usable.Sum(seen => seen.MarketPerDay * seen.Days);

        // Sale rates lag, and my own sales may not be inside them yet, so the arithmetic can
        // come out above one. A share of more than the whole market is a fault in the
        // measurement rather than a triumph.
        return moved > 0 ? Math.Min(1d, mine / moved) : null;
    }
}

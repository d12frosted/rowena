namespace Rowena.Core.Market;

/// <summary>What kind of market this is, which is a different question from what it pays.</summary>
public enum MarketCharacter
{
    /// <summary>Selling about as fast as it is stocked. Money now, and company.</summary>
    Hot,

    /// <summary>Working normally: it moves, the price holds, nobody is fighting over it.</summary>
    Steady,

    /// <summary>Slow, but thin enough that turning up at all is most of the work.</summary>
    Niche,

    /// <summary>The price moves enough that any margin taken off it has a wide error bar.</summary>
    Swingy,

    /// <summary>More stock listed than the board will get through in any useful time.</summary>
    Glutted,

    /// <summary>Nothing sells here at any price.</summary>
    Dead,

    /// <summary>How fast it sells is not known yet.</summary>
    Unknown,
}

/// <summary>
/// The character of a market, and the numbers behind it.
/// </summary>
/// <param name="DaysOfSupply">
/// How long the stock already listed would last at the rate it sells. The single most useful
/// number about a market: it is what is between you and a sale.
/// </param>
/// <param name="Spread">
/// How much recent prices vary, against the middle of them. Null when nothing has sold.
/// </param>
public readonly record struct MarketNature(MarketCharacter Character, double? DaysOfSupply, double? Spread)
{
    /// <summary>Under this many days of stock ahead of you, it is selling as fast as it is listed.</summary>
    private const double HotDays = 2d;

    /// <summary>Past this, the stock in front of you is the whole story.</summary>
    private const double GluttedDays = 30d;

    /// <summary>A market slower than this a day is not a busy one, whatever else it is.</summary>
    private const double SlowPerDay = 2d;

    /// <summary>More sellers than this and a slow market is contested rather than empty.</summary>
    private const int FewListed = 20;

    /// <summary>Prices varying by more than this share of the middle are not a firm number.</summary>
    private const double WideSpread = 0.35d;

    /// <summary>
    /// Reads a market from what is listed, what sells, and what it has been selling for.
    /// </summary>
    /// <remarks>
    /// Ordered by how much each finding overrules the next. A board with a year of stock on it
    /// is a glut whatever its prices are doing, and one where nothing sells at all is not slow,
    /// it is shut: calling that a niche would dress up the worst rows in a table as
    /// opportunities.
    ///
    /// A wide spread outranks a thin market for the same reason. Thin and quiet reads as an
    /// invitation and wildly priced reads as a warning, and where both are true the warning is
    /// the one worth having: measured, a module listing at nine million against sales all over
    /// the place came out as a niche worth eight hundred thousand a day.
    /// </remarks>
    public static MarketNature Of(int listed, double salesPerDay, IReadOnlyList<long> recentSales)
    {
        var spread = Variation(recentSales);

        if (salesPerDay <= 0)
            return new MarketNature(MarketCharacter.Dead, null, spread);

        var days = listed / salesPerDay;

        var character = days switch
        {
            > GluttedDays => MarketCharacter.Glutted,
            < HotDays => MarketCharacter.Hot,
            _ when spread > WideSpread => MarketCharacter.Swingy,
            _ when salesPerDay < SlowPerDay && listed <= FewListed => MarketCharacter.Niche,
            _ => MarketCharacter.Steady,
        };

        return new MarketNature(character, days, spread);
    }

    /// <summary>
    /// How much recent prices vary, as a share of the middle one.
    /// </summary>
    /// <remarks>
    /// The middle half against the median, rather than a standard deviation against a mean. One
    /// silly sale in a run of ordinary ones is common on this board and would make a calm
    /// market look wild; the quartiles do not notice it.
    /// </remarks>
    private static double? Variation(IReadOnlyList<long> sales)
    {
        if (sales.Count < 4)
            return null;

        var sorted = sales.Order().ToArray();
        var median = sorted[sorted.Length / 2];

        if (median <= 0)
            return null;

        return (double)(sorted[sorted.Length * 3 / 4] - sorted[sorted.Length / 4]) / median;
    }
}

/// <summary>
/// What kind of market something trades in.
/// </summary>
/// <remarks>
/// Every table here ranks by what a thing pays, which answers half the question. The other
/// half is what sort of market it is, and two rows paying the same are not the same
/// proposition: one may be selling as fast as it is stocked and the other may have two hundred
/// days of other people's stock in front of it.
///
/// The number that carries most of this is days of supply, what is listed over what sells.
/// Everything else here is a qualification of it.
///
/// The thresholds are judgements rather than measurements, and they are named and gathered
/// here so they can be argued with instead of being scattered through a ranking as bare
/// numbers.
/// </remarks>

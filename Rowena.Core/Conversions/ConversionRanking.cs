using Rowena.Core.Market;

namespace Rowena.Core.Conversions;

/// <summary>What a trade is worth per day, once the market's appetite is taken into account.</summary>
/// <param name="RunsPerDay">
/// How often the trade can be repeated before the output stops selling. Zero when nothing
/// sells, which is the honest answer rather than an inconvenient one.
/// </param>
public sealed record ExpectedEarnings(
    Conversion Conversion,
    ConversionQuote Quote,
    double RunsPerDay,
    long GilPerDay);

/// <summary>
/// Ranks trades by what they would actually earn in a day.
/// </summary>
/// <remarks>
/// Ranking by margin is what every craft-profit tool does and it is why their output cannot be
/// acted on. Margin floats to the top exactly the items with a wide spread and no buyers: a
/// two million gil margin on something that sells once a fortnight looks like the best row on
/// the screen and is worth about a hundred and forty thousand a day.
///
/// Multiplying by how fast the output actually moves inverts that ordering, and the inversion
/// is the entire point. A fifty thousand margin that clears twenty a day beats it fivefold.
///
/// Loss is left signed rather than clamped, so a trade that loses money quickly sorts below one
/// that loses it slowly. Both are worth avoiding and the ordering says which more urgently.
/// </remarks>
public static class ConversionRanking
{
    /// <summary>
    /// Prices every conversion and orders them by expected gil per day, best first.
    /// </summary>
    /// <param name="maxRunsPerDay">
    /// How many times you could actually perform the trade in a day, if that is the tighter
    /// limit. Without it the only ceiling is what the market will absorb, which flatters
    /// anything cheap and fast to produce.
    /// </param>
    public static IReadOnlyList<ExpectedEarnings> ByGilPerDay(
        IEnumerable<Conversion> conversions,
        Func<uint, OrderBook?> books,
        MarketTax tax,
        double? maxRunsPerDay = null) =>
        ByGilPerDay(conversions, books, books, tax, maxRunsPerDay);

    /// <summary>
    /// Ranks where buying and selling happen on different boards.
    /// </summary>
    /// <remarks>
    /// This is where the split matters most. Demand is what the ranking multiplies by, and the
    /// demand that counts is the one on the board your retainer stands on, not the whole data
    /// centre's.
    /// </remarks>
    public static IReadOnlyList<ExpectedEarnings> ByGilPerDay(
        IEnumerable<Conversion> conversions,
        Func<uint, OrderBook?> buying,
        Func<uint, OrderBook?> selling,
        MarketTax tax,
        double? maxRunsPerDay = null) =>
    [
        .. conversions
            .Select(conversion => For(
                conversion,
                ConversionEvaluator.Evaluate(conversion, 1, buying, selling, tax),
                maxRunsPerDay))
            .OrderByDescending(earnings => earnings.GilPerDay),
    ];

    /// <summary>Turns an already-priced quote into an expected daily figure.</summary>
    public static ExpectedEarnings For(Conversion conversion, ConversionQuote quote, double? maxRunsPerDay = null)
    {
        // DaysToAbsorb is how long the market takes to swallow one run's output, so its
        // reciprocal is how many runs a day it will take. Null means nothing sells at all.
        var runsPerDay = quote.DaysToAbsorb is { } days && days > 0d ? 1d / days : 0d;

        if (maxRunsPerDay is { } cap)
            runsPerDay = Math.Min(runsPerDay, Math.Max(0d, cap));

        return new ExpectedEarnings(conversion, quote, runsPerDay, (long)(quote.Profit * runsPerDay));
    }
}

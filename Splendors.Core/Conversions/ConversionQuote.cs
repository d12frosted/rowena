using Splendors.Core.Market;

namespace Splendors.Core.Conversions;

/// <summary>What a conversion is worth right now, at the prices actually on the board.</summary>
/// <param name="GilOutlay">
/// Cost of the item inputs, priced by walking the book rather than multiplying the floor.
/// </param>
/// <param name="GrossProceeds">Outputs valued at the current floor, before tax.</param>
/// <param name="NetProceeds">What lands in your pocket after the board takes its cut.</param>
/// <param name="CurrencySpent">
/// Bound currency consumed. This never costs gil, which is exactly why it needs reporting
/// separately: the whole question for a scrip sink is gil per scrip, and that ratio is
/// meaningless unless the denominator is tracked.
/// </param>
/// <param name="Unsourced">Item inputs the board could not supply in full.</param>
/// <param name="Unpriced">Outputs with nothing listed, so no price to value them at.</param>
/// <param name="DaysToAbsorb">
/// How long the market would take to swallow the outputs at the observed rate. A margin
/// you cannot sell into is not a margin.
/// </param>
public sealed record ConversionQuote(
    Conversion Conversion,
    int Runs,
    long GilOutlay,
    long GrossProceeds,
    long NetProceeds,
    IReadOnlyList<ResourceAmount> CurrencySpent,
    IReadOnlyList<ResourceAmount> Unsourced,
    IReadOnlyList<ResourceAmount> Unpriced,
    double? DaysToAbsorb)
{
    /// <summary>Net proceeds less what the inputs cost.</summary>
    public long Profit => NetProceeds - GilOutlay;

    /// <summary>Profit as a fraction of gil put in. Null when nothing was bought.</summary>
    public double? ReturnOnOutlay => GilOutlay == 0 ? null : (double)Profit / GilOutlay;

    /// <summary>
    /// True when every input could be sourced and every output could be priced. A quote
    /// that is not executable is still worth showing, but it is a projection, not a trade.
    /// </summary>
    public bool IsExecutable => Unsourced.Count == 0 && Unpriced.Count == 0;

    /// <summary>
    /// Gil earned per unit of a bound currency. The number that ranks scrip sinks against
    /// each other, and the only fair way to compare a sink to the time it took to earn it.
    /// </summary>
    public double? GilPer(Resource currency)
    {
        var spent = CurrencySpent
            .Where(amount => amount.Resource == currency)
            .Sum(amount => amount.Quantity);

        return spent == 0 ? null : (double)Profit / spent;
    }
}

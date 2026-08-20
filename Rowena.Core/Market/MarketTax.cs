namespace Rowena.Core.Market;

/// <summary>
/// The cut the market board takes, and it takes one from each side.
/// </summary>
/// <remarks>
/// The buyer pays a flat 5% on top of every listing, charged per listing. The seller
/// receives the listed price less 0 to 5% depending on the city the retainer stands in,
/// modelled here at the maximum, which is what the three original city states always
/// charge; parking retainers somewhere cheaper is a real edge this deliberately does not
/// assume. Both cuts share one rate today, so both live on this one value.
///
/// Truncation rather than rounding is not a guess. A recorded Universalis listing of
/// 146,385 gil carries a tax of 7,319, and 146,385 x 0.05 is 7,319.25, so the fraction
/// is dropped; another at 7,499,990 carries 374,999 where 5% is exactly 374,999.5, so
/// even the half goes. See <c>MarketTaxTests</c>, which pins both against the fixtures.
/// </remarks>
public readonly record struct MarketTax(double Rate)
{
    /// <summary>The current flat 5%.</summary>
    public static readonly MarketTax Standard = new(0.05);

    /// <summary>No tax, for venues that do not charge one and for isolating it in tests.</summary>
    public static readonly MarketTax None = new(0d);

    /// <summary>
    /// The cut on <paramref name="gross"/> gil. What the buyer pays on top of a listing,
    /// and what comes off the seller's side of a sale: the board charges both, floored.
    /// </summary>
    public long On(long gross) => (long)(gross * Rate);

    /// <summary>What the seller is left with after a sale of <paramref name="gross"/>.</summary>
    public long NetProceeds(long gross) => gross - On(gross);
}

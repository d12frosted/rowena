namespace Splendors.Core.Market;

/// <summary>
/// The cut the market board takes on a sale.
/// </summary>
/// <remarks>
/// Modelled as coming out of the seller's proceeds, which is how gil-making arithmetic
/// is normally done: you list at a price and receive less than it. If that turns out to
/// be backwards for some venue, change it here rather than sprinkling 0.95 through the
/// conversion code.
///
/// Truncation rather than rounding is not a guess. A recorded Universalis listing of
/// 146,385 gil carries a tax of 7,319, and 146,385 x 0.05 is 7,319.25, so the fraction
/// is dropped. See <c>MarketTaxTests</c>, which pins this against that fixture.
/// </remarks>
public readonly record struct MarketTax(double Rate)
{
    /// <summary>The current flat 5%.</summary>
    public static readonly MarketTax Standard = new(0.05);

    /// <summary>No tax, for venues that do not charge one and for isolating it in tests.</summary>
    public static readonly MarketTax None = new(0d);

    /// <summary>Gil taken off a sale of <paramref name="gross"/>.</summary>
    public long On(long gross) => (long)(gross * Rate);

    /// <summary>What the seller is left with after a sale of <paramref name="gross"/>.</summary>
    public long NetProceeds(long gross) => gross - On(gross);
}

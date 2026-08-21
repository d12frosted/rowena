namespace Rowena.Core.Market;

/// <summary>
/// The cut the market board takes, and it takes one from each side.
/// </summary>
/// <remarks>
/// The buyer pays a flat 5% on top of every listing, charged per listing. The seller
/// receives the listed price less 0 to 5% depending on the city the retainer stands in, and
/// that one moves daily. The two are separate numbers because only one of them is fixed:
/// <see cref="Standard"/> assumes the maximum on the selling side, which is what the three
/// original city states always charge, and the game itself corrects it once it has said
/// what the rates are.
///
/// Truncation rather than rounding is not a guess. A recorded Universalis listing of
/// 146,385 gil carries a tax of 7,319, and 146,385 x 0.05 is 7,319.25, so the fraction
/// is dropped; another at 7,499,990 carries 374,999 where 5% is exactly 374,999.5, so
/// even the half goes. See <c>MarketTaxTests</c>, which pins both against the fixtures.
/// </remarks>
/// <param name="BuyerRate">Charged on top of a listing. Flat, and not a city's business.</param>
/// <param name="SellerRate">Taken out of the proceeds, by the retainer's city.</param>
public readonly record struct MarketTax(double BuyerRate, double SellerRate)
{
    /// <summary>The flat 5% on top, and the worst the selling side can be.</summary>
    public static readonly MarketTax Standard = new(0.05, 0.05);

    /// <summary>No tax, for venues that do not charge one and for isolating it in tests.</summary>
    public static readonly MarketTax None = new(0d, 0d);

    /// <summary>What the buyer pays on top of a listing of <paramref name="gross"/>, floored.</summary>
    public long OnPurchase(long gross) => (long)(gross * BuyerRate);

    /// <summary>What the city takes out of a sale of <paramref name="gross"/>, floored.</summary>
    public long OnSale(long gross) => (long)(gross * SellerRate);

    /// <summary>What the seller is left with after a sale of <paramref name="gross"/>.</summary>
    public long NetProceeds(long gross) => gross - OnSale(gross);

    /// <summary>The same tax with the selling side the game has actually reported.</summary>
    public MarketTax WithSellerRate(double rate) => this with { SellerRate = rate };
}

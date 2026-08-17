namespace Rowena.Game;

/// <summary>
/// Which board to price against.
/// </summary>
/// <remarks>
/// One place, because there were briefly two. The window worked it out fresh every frame while
/// the fetcher had captured it once at construction, so the header could say "pricing against
/// Light" whilst every request went out with an empty scope and came back 404. Anything that
/// needs to know asks this.
/// </remarks>
internal sealed class PricingScope(Configuration config, Balances balances)
{
    /// <summary>
    /// Where you buy: the whole data centre.
    /// </summary>
    /// <remarks>
    /// Listings sit on individual worlds and you can travel to any of them, so the cheapest material
    /// anywhere on the data centre is one you can actually go and get. Optimistic only by the cost of
    /// the trip.
    /// </remarks>
    public string? Buying =>
        string.IsNullOrWhiteSpace(config.Scope) ? balances.DataCentre : config.Scope;

    /// <summary>
    /// Where you sell: your own world.
    /// </summary>
    /// <remarks>
    /// A retainer sells where it stands, so the price you get and the demand you meet are your home
    /// world's, not the data centre's. Using the data centre for both combined its cheapest listing
    /// with its total demand, and the second half of that was badly wrong: measured on Light, a Glade
    /// Bench fetches 57% more at home and sells a tenth as often.
    /// </remarks>
    public string? Selling =>
        string.IsNullOrWhiteSpace(config.HomeScope) ? balances.HomeWorld : config.HomeScope;

    /// <summary>Both known, which is what pricing anything needs.</summary>
    public bool Ready => Buying is not null && Selling is not null;
}

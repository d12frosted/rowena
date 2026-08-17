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
    /// The configured scope, or wherever you are logged in. Null when neither is known, which
    /// is a real state worth showing rather than papering over with a default.
    /// </summary>
    public string? Current =>
        string.IsNullOrWhiteSpace(config.Scope) ? balances.DataCentre : config.Scope;
}

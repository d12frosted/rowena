using Rowena.Core.Market;
using Rowena.Game;

namespace Rowena.Market;

/// <summary>
/// The two books a quote needs: where the inputs come from, and where the outputs go.
/// </summary>
/// <remarks>
/// One type because the asymmetry is the whole point and every view needs both halves of it. You buy
/// across the data centre, since you can travel to any world on it, and you sell on your own, since a
/// retainer sells where it stands. Pricing both sides against one board is what made the old numbers
/// wrong: it combined the cheapest listing anywhere with the demand of everywhere.
/// </remarks>
internal sealed class Boards(MarketCache market, PricingScope scope)
{
    /// <summary>Where the inputs come from.</summary>
    public Func<uint, OrderBook?> Buying => market.Lookup(scope.Buying ?? "");

    /// <summary>Where the outputs go.</summary>
    public Func<uint, OrderBook?> Selling => market.Lookup(scope.Selling ?? "");
}

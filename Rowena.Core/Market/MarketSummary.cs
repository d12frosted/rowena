namespace Rowena.Core.Market;

/// <summary>
/// The cheap answer about an item: what it goes for and how fast it moves.
/// </summary>
/// <remarks>
/// Enough to decide whether an item is worth investigating and not enough to trade on. Deliberately
/// so: asking for full books is expensive, and most of what a survey covers turns out not to be
/// worth the request.
///
/// Measured against Light, a hundred items summarised comes back in under two seconds, while ten
/// items with their listings times out at the ten-second gateway limit. That is the entire reason
/// this type exists.
/// </remarks>
public readonly record struct MarketSummary(uint ItemId, long? Floor, double SaleVelocityPerDay)
{
    /// <summary>
    /// The most the whole market could turn over in a day at the asking price.
    /// </summary>
    /// <remarks>
    /// A ceiling on what anyone can earn from the item, which makes it a sound way to decide what
    /// not to bother costing: nothing can pay more per day than the board turns over.
    /// </remarks>
    public double DailyRevenue => Floor is { } floor ? floor * SaleVelocityPerDay : 0d;

    public bool Trades => Floor is not null && SaleVelocityPerDay > 0d;
}

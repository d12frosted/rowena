namespace Rowena.Core.Market;

/// <summary>One of my own sales, as it happened.</summary>
/// <param name="Gil">What it fetched, net of the fees the game had already taken off.</param>
/// <param name="Announced">
/// True when the game said so in chat, false when it was worked out from a retainer's slots
/// and purse. Kept apart because one is a fact and the other is a reading.
/// </param>
public readonly record struct SaleRecord(uint ItemId, int Quantity, long Gil, DateTimeOffset At, bool Announced = true)
{
    /// <summary>What one of them fetched.</summary>
    public long Each => Quantity > 0 ? Gil / Quantity : Gil;
}

/// <summary>
/// How long my own sales are remembered.
/// </summary>
/// <remarks>
/// By age, because the questions asked of the record are about time: what has been clearing
/// this month, what sat all spring. A count of five hundred was a fortnight for a busy
/// retainer and a year for an idle one, which is no answer to either. The cap underneath is
/// a safety net against a file that grows without bound, set far above anything a few
/// retainers produce in the window.
/// </remarks>
public static class SalesRetention
{
    public static IReadOnlyList<SaleRecord> Prune(
        IEnumerable<SaleRecord> sales,
        DateTimeOffset now,
        int keepDays,
        int cap)
    {
        var cutoff = now.AddDays(-Math.Max(1, keepDays));

        return
        [
            .. sales
                .Where(sale => sale.At >= cutoff)
                .OrderByDescending(sale => sale.At)
                .Take(Math.Max(1, cap)),
        ];
    }
}

namespace Rowena.Core.Market;

/// <summary>One of a retainer's market slots, or nothing when the slot is empty.</summary>
public readonly record struct MarketSlot(uint ItemId, int Quantity, long UnitPrice, bool IsHq = false)
{
    public bool IsEmpty => ItemId == 0 || Quantity <= 0;
}

/// <summary>A sale nobody announced, worked out from what changed.</summary>
public readonly record struct InferredSale(uint ItemId, int Quantity, long Gross, long Net);

/// <summary>
/// What sold while nobody was watching.
/// </summary>
/// <remarks>
/// A sale announced in chat is only a sale that happened while logged in. The rest are found
/// the only way they can be: by looking at a retainer's market slots, comparing them with how
/// they were left, and reading the purse.
///
/// The purse is the whole of it. A listing that sold and a listing that was taken off the
/// market look exactly the same from the slots, and calling the second one a sale would
/// quietly invent income. Gil arriving is what separates them.
///
/// The budget is spent down as sales are attributed, which matters when several things go
/// between visits: checking each vanished slot against the same untouched purse lets one
/// thing's worth of gil pay for two things.
///
/// What was already announced has to come off the top. The slots cannot tell a sale I was told
/// about from one I was not, since both leave an empty slot and gil in the purse, so without
/// subtracting them every sale heard in chat would be counted again the next time I opened that
/// retainer and my takings would quietly double.
/// </remarks>
public static class SaleReconciliation
{
    /// <summary>
    /// Works out what sold between two looks at the same retainer.
    /// </summary>
    /// <param name="gilGained">
    /// What the retainer's purse gained. Negative means gil was taken out, which explains
    /// nothing and is worth no guesses: read as an unsigned difference it becomes an enormous
    /// number and every empty slot turns into a sale.
    /// </param>
    /// <param name="alreadyKnown">
    /// How much of each item the game has already announced since the last look, which is not
    /// news and must not be booked twice.
    /// </param>
    public static IReadOnlyList<InferredSale> Between(
        IReadOnlyList<MarketSlot> before,
        IReadOnlyList<MarketSlot> after,
        long gilGained,
        MarketTax tax,
        IReadOnlyDictionary<uint, int>? alreadyKnown = null)
    {
        if (before.Count == 0 || gilGained <= 0)
            return [];

        var sales = new List<InferredSale>();
        var budget = gilGained;
        var known = alreadyKnown is null
            ? []
            : new Dictionary<uint, int>(alreadyKnown);

        for (var slot = 0; slot < before.Count && slot < after.Count; slot++)
        {
            var was = before[slot];

            if (was.IsEmpty)
                continue;

            var now = after[slot];

            // Slots get reused. Something else standing where mine was says nothing about what
            // happened to mine, so it is left alone rather than guessed at.
            int gone = now.IsEmpty
                ? was.Quantity
                : now.ItemId == was.ItemId ? was.Quantity - now.Quantity : 0;

            // Whatever the game already said about this item is not news. Spent down as it is
            // used, so two slots of the same thing cannot both be excused by one announcement.
            if (known.TryGetValue(was.ItemId, out var told) && told > 0)
            {
                var covered = Math.Min(told, gone);

                known[was.ItemId] = told - covered;
                gone -= covered;
            }

            if (gone <= 0)
                continue;

            var gross = was.UnitPrice * gone;
            var net = tax.NetProceeds(gross);

            if (net > budget)
                continue;

            budget -= net;
            sales.Add(new InferredSale(was.ItemId, gone, gross, net));
        }

        return sales;
    }
}

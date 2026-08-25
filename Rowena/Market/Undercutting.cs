using Rowena.Core.Market;

namespace Rowena.Market;

/// <summary>
/// The undercut price for any of my listings, with my own exceptions applied.
/// </summary>
/// <remarks>
/// One place, because two views ask the same question: the Selling tab when it draws a row,
/// and the retainer's price dialog when it opens on that row's item. They had better agree.
/// </remarks>
internal sealed class Undercutting(Boards boards, Configuration config, Action save)
{
    /// <summary>The price this listing wants to be at, or null when it is already right: under whoever is in front, at what people pay, or up under the next listing when there is real room.</summary>
    public UndercutPlan? Plan(uint itemId, long mine, bool hq = false) =>
        Undercut.Of(mine, boards.Selling(itemId), config.UndercutBy, hq);

    public bool Ignored(uint itemId) => config.UndercutIgnored.Contains(itemId);

    public void Ignore(uint itemId, bool ignore)
    {
        if (ignore == Ignored(itemId))
            return;

        if (ignore)
            config.UndercutIgnored.Add(itemId);
        else
            config.UndercutIgnored.Remove(itemId);

        save();
    }

    /// <summary>
    /// Forgets the ignores for anything no longer listed.
    /// </summary>
    /// <remarks>
    /// An ignore was a decision about a stack that was out. Once it has gone, sold or taken
    /// back, the next stack of the same thing is a new question, and starts watched.
    /// </remarks>
    public void Prune(IReadOnlyCollection<uint> listed)
    {
        var removed = config.UndercutIgnored.RemoveAll(itemId => !listed.Contains(itemId));

        if (removed > 0)
            save();
    }

    /// <summary>The plan, unless I have said to leave this item alone.</summary>
    public UndercutPlan? Wanted(uint itemId, long mine, bool hq = false) =>
        Ignored(itemId) ? null : Plan(itemId, mine, hq);

    /// <summary>
    /// What a listing wants doing, and whether it is worth doing.
    /// </summary>
    /// <remarks>
    /// The two together because they are read together and must agree: the price that would put
    /// me first is worth nothing without the size of the move that gets there. Both come off one
    /// book for the same reason the plan and the diagnosis share their thresholds.
    /// </remarks>
    /// <param name="tax">
    /// The cut for the city this listing actually stands in, where the caller knows it. Without
    /// one, the worst of the cities I keep retainers in, which is what everything else assumes.
    /// </param>
    public (UndercutPlan Plan, ChaseVerdict Chase)? Wants(
        uint itemId,
        long mine,
        bool hq = false,
        MarketTax? tax = null)
    {
        if (boards.Selling(itemId) is not { } book || Undercut.Of(mine, book, config.UndercutBy, hq) is not { } plan)
            return null;

        return (plan, Chase.Of(plan, book, tax ?? boards.Tax, config.SellingHorizon(), hq));
    }
}

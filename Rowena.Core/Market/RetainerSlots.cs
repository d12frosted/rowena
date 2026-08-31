namespace Rowena.Core.Market;

/// <summary>A stack that could be given a retainer's market slot.</summary>
/// <param name="Units">How many of it one slot would hold, which is a stack rather than a pile.</param>
/// <param name="Worth">What those units fetch if all of them sell.</param>
/// <param name="Realised">What actually sells inside the horizon, worked out by whoever priced the stack.</param>
public readonly record struct SlotCandidate(uint ItemId, int Units, long Worth, long Realised);

/// <summary>A stack worth a slot, and what the slot earns for holding it.</summary>
/// <param name="Realised">What actually sells inside the horizon, which is what a slot is worth.</param>
public readonly record struct SlotPick(uint ItemId, int Units, long Realised, long Worth);

/// <summary>
/// What to put in a retainer's market slots.
/// </summary>
/// <remarks>
/// There are twenty per retainer and always more worth selling than slots to sell it from, so
/// something has to choose, and the obvious choice is wrong. Ranked on what a stack is worth,
/// the big slow piles win every slot and then sit in them for months.
///
/// A slot is a rate rather than a lump. What it earns is what actually sells while it is
/// occupied, so a stack worth a hundred and twenty thousand that takes a hundred and sixteen
/// days to clear earns a week of that slot about seven thousand, and a stack worth forty-six
/// thousand that clears in a morning earns all of it and then frees the slot.
///
/// That one change is also the portfolio: nothing has to impose a quota of fast movers against
/// slow ones, because pricing the slot by what it turns over prefers the mix on its own.
/// </remarks>
public static class RetainerSlots
{
    /// <summary>
    /// Fills the slots there are with the stacks that earn the most from them.
    /// </summary>
    /// <remarks>
    /// The ranking only. What a candidate realises is worked out where the stack was priced,
    /// because the same number decides whether it is worth listing at all, and two copies of
    /// that arithmetic would eventually disagree about the same stack.
    /// </remarks>
    public static IReadOnlyList<SlotPick> Fill(IEnumerable<SlotCandidate> candidates, int slots)
    {
        if (slots <= 0)
            return [];

        return
        [
            .. candidates
                .Where(candidate => candidate.Realised > 0)
                .OrderByDescending(candidate => candidate.Realised)
                .Take(slots)
                .Select(candidate => new SlotPick(
                    candidate.ItemId, candidate.Units, candidate.Realised, candidate.Worth)),
        ];
    }

    /// <summary>What the plan earns over the horizon, which is not what its stacks are worth.</summary>
    public static long Earns(IEnumerable<SlotPick> picks) => picks.Sum(pick => pick.Realised);

    /// <summary>
    /// The share of a stack that sells while the slot holds it.
    /// </summary>
    /// <remarks>
    /// Something that never sells earns nothing, however dear it is. That is not the same as
    /// being worthless: it is worth whatever a vendor pays, and no slot at all.
    ///
    /// Lives here because a slot is what it is a measure of, and is used where a stack is
    /// priced rather than where the slots are handed out: the same number decides whether a
    /// stack is worth listing at all, and a stack called too small to list while the plan lists
    /// it would be worse than either answer.
    /// </remarks>
    public static long Realised(long worth, double? daysToClear, double horizonDays) =>
        worth > 0 && daysToClear is { } days and > 0
            ? (long)(worth * Math.Min(1d, horizonDays / days))
            : 0;

}

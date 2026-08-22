namespace Rowena.Core.Market;

/// <summary>A stack that could be given a retainer's market slot.</summary>
/// <param name="Worth">What the whole stack fetches if all of it sells.</param>
/// <param name="DaysToClear">How long that would take, or null when it never would.</param>
public readonly record struct SlotCandidate(uint ItemId, int Units, long Worth, double? DaysToClear);

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
    /// <param name="horizonDays">How long I am willing to be selling, which is what a slot is judged over.</param>
    public static IReadOnlyList<SlotPick> Fill(
        IEnumerable<SlotCandidate> candidates,
        int slots,
        double horizonDays)
    {
        if (slots <= 0 || horizonDays <= 0)
            return [];

        return
        [
            .. candidates
                .Select(candidate => new SlotPick(
                    candidate.ItemId,
                    candidate.Units,
                    Realised(candidate, horizonDays),
                    candidate.Worth))
                .Where(pick => pick.Realised > 0)
                .OrderByDescending(pick => pick.Realised)
                .Take(slots),
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
    /// </remarks>
    private static long Realised(SlotCandidate candidate, double horizonDays) =>
        candidate is { Worth: > 0, DaysToClear: { } days and > 0 }
            ? (long)(candidate.Worth * Math.Min(1d, horizonDays / days))
            : 0;
}

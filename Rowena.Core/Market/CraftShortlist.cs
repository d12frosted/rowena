namespace Rowena.Core.Market;

/// <summary>Something the survey saw, at the depth a survey can see it.</summary>
/// <param name="Revenue">What the whole market turns over in a day, which is the cheap ranking.</param>
public readonly record struct Candidate(string Id, double Revenue, long Floor, double SalesPerDay);

/// <summary>
/// Which of thousands of candidates are worth the expensive half of a sweep.
/// </summary>
/// <remarks>
/// The survey is wide and cheap; costing a recipe's materials is narrow and dear, so something
/// has to choose. Ranking on what a market turns over in a day is the obvious choice and it
/// has a blind spot built into it: a thing selling once a day for two million turns over less
/// than a trinket selling three hundred times, so it is never costed and nobody ever finds out
/// what it pays.
///
/// That blind spot is exactly where a person can still win. Busy markets are busy because
/// everybody can see them, and a market that turns over little because almost nobody wants it
/// is also a market almost nobody is supplying. So some of the shortlist is set aside for the
/// dearest quiet things, which no amount of ranking on turnover would ever reach.
///
/// Quiet is not the same as shut, and the line between them has to be drawn well clear of
/// zero. Ranked on price alone the reserved slots fill with parked items: something listed at
/// a billion gil that nobody has ever bought is the dearest thing on the board and the least
/// worth costing. A survey cannot tell a fantasy price from a real one, because it has no sale
/// history to judge against, but it can tell a market that barely moves from one that does not
/// move at all. So a niche has to be selling at least occasionally, and a board quieter than
/// one sale in five days is not a niche anybody can work.
/// </remarks>
public static class CraftShortlist
{
    /// <summary>Quieter than one sale in five days is not a niche, it is a shut market.</summary>
    private const double WorkablePerDay = 0.2d;

    /// <summary>
    /// Picks what to cost: the busiest markets, and a reserved slice of the quietest dear ones.
    /// </summary>
    /// <param name="busy">How many to take on turnover alone.</param>
    /// <param name="niche">How many to set aside for quiet markets that turnover would never reach.</param>
    /// <param name="slowPerDay">Below this many sales a day, a market counts as quiet.</param>
    /// <remarks>
    /// Quiet has a lower bound as well as an upper one. Without it the dearest thing nobody
    /// buys wins every reserved slot, which is the same fantasy-price problem the order book
    /// guards against, arriving a step earlier where there is no history to catch it with.
    /// </remarks>
    public static IReadOnlyList<string> Pick(
        IEnumerable<Candidate> candidates,
        int busy,
        int niche,
        double slowPerDay)
    {
        var all = candidates as IReadOnlyList<Candidate> ?? [.. candidates];

        var picked = new List<string>(
            all.Where(candidate => candidate.Revenue > 0)
                .OrderByDescending(candidate => candidate.Revenue)
                .Take(Math.Max(0, busy))
                .Select(candidate => candidate.Id));

        if (niche <= 0)
            return picked;

        var taken = picked.ToHashSet(StringComparer.Ordinal);

        picked.AddRange(
            all.Where(candidate =>
                    candidate.SalesPerDay >= WorkablePerDay
                    && candidate.SalesPerDay < slowPerDay
                    && candidate.Floor > 0
                    && !taken.Contains(candidate.Id))
                .OrderByDescending(candidate => candidate.Floor)
                .Take(niche)
                .Select(candidate => candidate.Id));

        return picked;
    }
}

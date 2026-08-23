namespace Rowena.Core.Market;

/// <summary>What a session is trying to be good at, since these pull against each other.</summary>
public enum GatherAim
{
    /// <summary>The dearest things the board has room for, however few that turns out to be.</summary>
    MostGil,

    /// <summary>The same, with no one thing allowed to be most of the trip.</summary>
    MixedBag,

    /// <summary>Only what the board will take tomorrow, so none of it sits.</summary>
    SellsSoonest,
}

/// <summary>Something worth gathering, reduced to what a plan needs to know.</summary>
/// <param name="Net">What one is worth once sold, after the market's cut.</param>
/// <param name="SalesPerDay">How fast the board takes them, which limits how many are worth having.</param>
/// <param name="Listed">How many are already for sale, and so are ahead of yours in the queue.</param>
/// <param name="Timed">Whether the node appears on a clock, which changes what it costs to visit.</param>
/// <param name="Held">How many I already have and have not listed: bags and retainers. They go out before anything gathered does.</param>
public readonly record struct GatherCandidate(
    uint ItemId,
    long Net,
    double SalesPerDay,
    int Listed,
    bool Timed = false,
    int Held = 0);

/// <summary>How many of one thing to gather, what they come to, and what they cost the session.</summary>
public readonly record struct GatherPortion(uint ItemId, int Units, long Gil, double Cost);

/// <summary>
/// What to gather in the time you actually have.
/// </summary>
/// <remarks>
/// The ranking answers "what is worth gathering" and leaves the harder question alone: a
/// session is an hour, not a day, and the best hour is not simply the best row repeated
/// until the hour is up.
///
/// Everything is measured in the same currency, an ordinary item's worth of time, so that
/// unlike things can be compared. An untimed item costs one of those, because that is what
/// the unit is. A node on a clock costs whatever the trip to it costs and gives back a
/// windowful, which is usually a bargain and is exactly why timed nodes are worth the
/// detour: pricing them this way lets them compete on their merits rather than being ranked
/// as though a window were an hour or dropped for not fitting the assumption.
///
/// What the board will take is also not what it will take from you. A hundred and thirty a
/// day is the whole market's throughput and the people already listing are ahead of you in
/// it, so the room for one more seller is what is left after them. And after your own pile:
/// nine hundred and ninety-nine of something in a retainer go out before anything gathered
/// today does, so the room they take is not room for gathering.
///
/// What it cannot know is how many items an hour actually yields, or what a window gives.
/// Those are measurements nobody has made, so they are numbers handed in from outside rather
/// than invented here, and everything below is honest about being scaled by them.
/// </remarks>
public static class GatherPlan
{
    /// <summary>No one thing may be more than this much of a mixed bag.</summary>
    private const double MixedBagShare = 0.25d;

    /// <summary>What "soonest" means: gather only what tomorrow's board will take.</summary>
    private const double SoonDays = 1d;

    /// <summary>
    /// Picks the basket worth bringing back.
    /// </summary>
    /// <param name="capacity">How many ordinary items the session is worth.</param>
    /// <param name="horizonDays">
    /// How long you are willing to be selling. What the board takes in that time is what is
    /// worth gathering; past it you are holding stock rather than earning.
    /// </param>
    /// <param name="windowYield">How many items one visit to a timed node is worth.</param>
    /// <param name="windowCost">What that visit costs, in ordinary items of time.</param>
    public static IReadOnlyList<GatherPortion> For(
        IEnumerable<GatherCandidate> candidates,
        int capacity,
        double horizonDays,
        GatherAim aim = GatherAim.MostGil,
        int windowYield = 40,
        double windowCost = 25d)
    {
        if (capacity <= 0 || horizonDays <= 0)
            return [];

        // Selling sooner is a shorter horizon rather than a different ranking. Ranked on turnover
        // instead, it bought three hundred fifty-gil crystals: the board moves seventy-five
        // thousand a day, so nothing else could ever outrank them, and a session worth nine
        // hundred thousand came back worth fifteen. What you want from "soonest" is the same good
        // haul with none of it left waiting, which is a smaller window on the same question.
        var days = aim == GatherAim.SellsSoonest ? Math.Min(SoonDays, horizonDays) : horizonDays;

        var offers = candidates
            .Select(candidate => Weigh(candidate, days, windowYield, windowCost))
            .OfType<Offer>()
            .OrderByDescending(offer => offer.Net / offer.CostPerUnit)
            .ToArray();

        // A quarter each, which is a floor on variety rather than a ceiling: four things at
        // least, and no single price move taking the whole trip with it.
        var share = aim == GatherAim.MixedBag ? capacity * MixedBagShare : capacity;

        var basket = new List<GatherPortion>();
        double left = capacity;

        foreach (var offer in offers)
        {
            if (left <= 0)
                break;

            var units = (int)Math.Floor(Math.Min(left, share) / offer.CostPerUnit);

            units = Math.Min(units, offer.MaxUnits);

            // A window is all of it or none of it: you cannot visit three quarters of a node,
            // and half a trip costs the same as the whole one.
            if (units <= 0 || (offer.WholeOrNothing && units < offer.MaxUnits))
                continue;

            var cost = units * offer.CostPerUnit;

            basket.Add(new GatherPortion(offer.ItemId, units, units * offer.Net, cost));
            left -= cost;
        }

        return basket;
    }

    /// <summary>What the basket comes to.</summary>
    public static long Worth(IEnumerable<GatherPortion> basket) => basket.Sum(portion => portion.Gil);

    /// <summary>
    /// How many days the board needs to get through what I already have of something.
    /// </summary>
    /// <remarks>
    /// Held and listed both, since both are mine and both go out before anything gathered.
    /// Null when it never would, which is a different answer from zero.
    /// </remarks>
    public static double? Backlog(int held, int listedMine, double salesPerDay)
    {
        var mine = Math.Max(0, held) + Math.Max(0, listedMine);

        if (mine == 0)
            return 0d;

        return salesPerDay > 0 ? mine / salesPerDay : null;
    }

    /// <summary>
    /// One candidate priced in time, or nothing when the board has no room for it.
    /// </summary>
    private static Offer? Weigh(GatherCandidate candidate, double horizonDays, int windowYield, double windowCost)
    {
        var room = (int)Math.Floor(candidate.SalesPerDay * horizonDays) - candidate.Listed - Math.Max(0, candidate.Held);

        if (candidate.Net <= 0 || room <= 0)
            return null;

        if (!candidate.Timed)
            return new Offer(candidate.ItemId, room, 1d, false, candidate.Net);

        var units = Math.Min(windowYield, room);

        return units <= 0 || windowCost <= 0
            ? null
            : new Offer(candidate.ItemId, units, windowCost / units, true, candidate.Net);
    }

    /// <summary>A candidate with its price in time worked out.</summary>
    private readonly record struct Offer(
        uint ItemId,
        int MaxUnits,
        double CostPerUnit,
        bool WholeOrNothing,
        long Net);
}

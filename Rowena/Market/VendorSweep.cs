using Dalamud.Plugin.Services;
using Rowena.Core.Market;
using Rowena.Game;

namespace Rowena.Market;

/// <summary>
/// Looks over the whole board for listings priced under what a vendor pays.
/// </summary>
/// <remarks>
/// The one trade with no market risk at all: buy it, walk to any vendor, sell it. It exists
/// because somebody lists a stack under the vendor price by mistake or to clear a retainer
/// slot, and nothing in the game or in any other tool points at it.
///
/// Sixteen thousand items cannot be fetched in full, so this is the same two passes as the
/// furnishing sweep and for the same reason: survey everything cheaply, then cost only what
/// survives. The filter here is sounder than the furnishing one, because a summary carries
/// the cheapest price and no listing in a book is cheaper than its floor. A floor that
/// already loses money after the buyer's cut cannot hide a bargain underneath it.
///
/// The shortlist is a list of item ids, and what those ids are worth is computed from the
/// cache wherever it is shown, so the numbers are as fresh as the last fetch rather than as
/// old as the scan. Nothing of its own goes to disk: the summaries are already persisted, so
/// a restart rebuilds the shortlist without a single request.
/// </remarks>
internal sealed class VendorSweep(VendorPrices vendors, MarketCache market, IPluginLog log)
{
    public enum Phase
    {
        Idle,
        Surveying,
        Pricing,
        Ready,

        /// <summary>Finished with holes: usable, and honest about not being complete.</summary>
        Partial,

        Failed,
    }

    /// <param name="Surveyed">How many items the last pass actually has an answer for.</param>
    /// <param name="Possible">Items whose cheapest listing was under the vendor price.</param>
    /// <param name="Shortlist">Those of them costed in full, which is what can be shown.</param>
    public sealed record Snapshot(
        Phase State,
        string Detail,
        int Candidates,
        int Surveyed,
        int Possible,
        IReadOnlyList<uint> Shortlist,
        DateTimeOffset? ReadyAt)
    {
        public bool Running => State is Phase.Surveying or Phase.Pricing;

        public bool HasResults => Shortlist.Count > 0;
    }

    /// <summary>
    /// The scan as one value, swapped whole.
    /// </summary>
    /// <remarks>
    /// A record rather than a field per fact, so the draw thread cannot read a finished state
    /// beside a shortlist that has not arrived yet. One reference assignment is atomic; a
    /// handful of separate ones are a race waiting for a slow frame.
    /// </remarks>
    public Snapshot Current { get; private set; } = new(Phase.Idle, "", 0, 0, 0, [], null);

    /// <summary>Starts a scan, or does nothing if one is already running.</summary>
    public void Start(string? buying, int toCost, TimeSpan maxAge)
    {
        if (Current.Running || string.IsNullOrWhiteSpace(buying))
            return;

        Current = Current with { State = Phase.Surveying, Detail = "reading the item sheet" };
        _ = Task.Run(() => Run(buying, toCost, maxAge));
    }

    /// <summary>
    /// Rebuilds the shortlist from summaries a previous session already fetched.
    /// </summary>
    /// <remarks>
    /// Free, and worth doing on open: the expensive half of a scan is the survey, and its
    /// answers come back off disk with the rest of the cache. What this cannot know is
    /// whether they are complete, so a restored shortlist reports the age of the data it
    /// was built from rather than claiming to be a scan.
    /// </remarks>
    public void RestoreOnce(string buying, int toCost)
    {
        if (Current.Running || Current.HasResults || Current.State != Phase.Idle)
            return;

        Current = Current with { State = Phase.Pricing, Detail = "reading what the cache already knows" };

        _ = Task.Run(() =>
        {
            try
            {
                var candidates = vendors.Sellable();
                var possible = Possible(buying, candidates, out var surveyed, out var at);
                var shortlist = Costliest(buying, possible, toCost);

                Current = surveyed == 0
                    ? new Snapshot(Phase.Idle, "", 0, 0, 0, [], null)
                    : new Snapshot(
                        Phase.Partial,
                        $"{possible.Count} worth a look, from {surveyed} of {candidates.Count} items the cache "
                        + "already knew. Scan for the rest.",
                        candidates.Count,
                        surveyed,
                        possible.Count,
                        shortlist,
                        at);

                if (surveyed > 0)
                    log.Information($"Rebuilt a vendor shortlist of {shortlist.Count} from cached summaries.");
            }
            catch (Exception error)
            {
                Current = new Snapshot(Phase.Idle, "", 0, 0, 0, [], null);
                log.Warning(error, "Could not rebuild the vendor shortlist.");
            }
        });
    }

    private async Task Run(string buying, int toCost, TimeSpan maxAge)
    {
        try
        {
            var candidates = vendors.Sellable();

            if (candidates.Count == 0)
            {
                Current = Current with { State = Phase.Failed, Detail = "no vendor prices in the item sheet" };
                return;
            }

            // Only what is missing or stale, which is what makes a re-scan resume rather than
            // start again. A first scan asks for everything; the next one asks for the gaps.
            var wanted = candidates.Where(id => market.SummaryIsStale(buying, id, maxAge)).ToArray();

            Current = Current with
            {
                Candidates = candidates.Count,
                Detail = wanted.Length == 0
                    ? "the cache already has every item"
                    : $"surveying {wanted.Length} of {candidates.Count} items",
            };

            if (wanted.Length > 0)
            {
                await market
                    .SurveyAsync(
                        buying, wanted, FetchPriority.Sweep,
                        (done, total) => Current = Current with { Detail = $"surveying: {done} of {total}" })
                    .ConfigureAwait(false);
            }

            var possible = Possible(buying, candidates, out var surveyed, out _);

            // Full books for the best of them, because a summary carries no depth and the
            // answer is how many units are under the vendor price, not merely that one is.
            // Capped, because a thousand candidates is a thousand costing requests; the
            // margin per unit is the only ranking a summary supports, and it is blind to
            // stack size, so the cap is said out loud rather than applied quietly.
            var shortlist = Costliest(buying, possible, toCost);
            var stale = shortlist.Where(id => market.IsStale(buying, id, maxAge)).ToArray();

            if (stale.Length > 0)
            {
                Current = Current with { State = Phase.Pricing, Detail = $"pricing {stale.Length} candidates" };

                await market
                    .PriceAsync(
                        buying, stale, FetchPriority.Sweep,
                        (done, total) => Current = Current with { Detail = $"pricing: {done} of {total}" })
                    .ConfigureAwait(false);
            }

            var missed = candidates.Count - surveyed;
            var capped = possible.Count > shortlist.Count
                ? $", costing the {shortlist.Count} widest margins of them"
                : "";

            Current = new Snapshot(
                missed > 0 ? Phase.Partial : Phase.Ready,
                missed > 0
                    ? $"{possible.Count} worth a look of {surveyed} items{capped}; {missed} never answered. "
                      + "Scanning again asks only for those."
                    : $"{possible.Count} worth a look of {candidates.Count} items{capped}",
                candidates.Count,
                surveyed,
                possible.Count,
                shortlist,
                DateTimeOffset.UtcNow);

            log.Information($"Vendor scan done: {possible.Count} candidates from {surveyed} items.");
        }
        catch (Exception error)
        {
            Current = Current with { State = Phase.Failed, Detail = error.Message };
            log.Error(error, "Vendor scan failed.");
        }
    }

    /// <summary>The items whose cheapest listing is under what a vendor pays.</summary>
    private IReadOnlyList<uint> Possible(
        string buying,
        IReadOnlyList<uint> candidates,
        out int surveyed,
        out DateTimeOffset? oldest)
    {
        var shortlist = new List<uint>();
        var seen = 0;

        foreach (var id in candidates)
        {
            if (market.Summary(buying, id) is not { Floor: { } floor })
                continue;

            seen++;

            if (VendorArbitrage.Possible(floor, vendors.For(id), MarketTax.Standard))
                shortlist.Add(id);
        }

        surveyed = seen;
        oldest = market.LastRefresh;
        return shortlist;
    }

    /// <summary>
    /// The candidates with the widest margin per unit, which is all a summary can rank on.
    /// </summary>
    private IReadOnlyList<uint> Costliest(string buying, IReadOnlyList<uint> possible, int toCost) =>
    [
        .. possible
            .OrderByDescending(id => vendors.For(id) - (market.Summary(buying, id)?.Floor ?? 0))
            .Take(Math.Max(1, toCost)),
    ];
}

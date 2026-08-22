using Dalamud.Plugin.Services;
using Rowena.Core.Conversions;
using Rowena.Core.Market;
using Rowena.Game;

namespace Rowena.Market;

/// <summary>
/// Prices the furnishing market in two passes so the second one only pays for candidates worth
/// costing.
/// </summary>
/// <remarks>
/// A naive sweep would price every furnishing and every ingredient of every furnishing, which at
/// twenty ids per request is a great many requests for a great many rows that were never going
/// to be worth crafting.
///
/// So: price the products first, then shortlist on revenue potential, the product's price times
/// how fast it sells, and cost only those. The filter is sound rather than merely cheap. You
/// cannot earn more from an item in a day than the whole board turns over in it, so anything
/// filtered out here could not have led the ranking however cheap its materials turned out to be.
///
/// The sheets are read on this thread rather than the framework one, following the same
/// reasoning as AutoKill's mob index: it is file-backed reference data, not client memory, and
/// walking every recipe row has no business blocking a frame.
/// </remarks>
internal sealed class FurnishingSweep(
    Furnishings furnishings,
    MarketCache market,
    Configuration config,
    IPluginLog log)
{
    public enum Phase
    {
        Idle,
        Products,
        Shortlisting,
        Ingredients,
        Ready,

        /// <summary>
        /// Finished, but with holes. Usable and honest about not being complete.
        /// </summary>
        /// <remarks>
        /// A run that lost most of its batches to timeouts looks identical to one that found
        /// nothing for sale, and reporting the second when the first happened is the worst kind of
        /// wrong: confident. This state exists so that never happens again.
        /// </remarks>
        Partial,

        Failed,
    }

    public Phase State { get; private set; } = Phase.Idle;

    /// <summary>Where the sweep has got to, for showing rather than for logic.</summary>
    public string Detail { get; private set; } = "";

    /// <summary>Every craftable tradable furnishing found in the sheets.</summary>
    public int Candidates { get; private set; }

    /// <summary>The ones worth costing, once the products were priced.</summary>
    public IReadOnlyList<Conversion> Shortlist { get; private set; } = [];

    public DateTimeOffset? ReadyAt { get; private set; }

    /// <summary>
    /// The materials standing between the shortlist and a price, commonest first.
    /// </summary>
    /// <remarks>
    /// This exists to settle one question with evidence instead of opinion: whether following
    /// recipes down to raw materials is worth building. Direct-ingredient pricing discards any
    /// furnishing whose materials are not on the board, and the tree would rescue precisely those
    /// whose blocker is itself craftable. If the blockers turn out to be restoration mats, map
    /// drops or untradables, no amount of tree walking helps and the work should not be done.
    /// </remarks>
    public IReadOnlyList<Blocker> Blockers { get; private set; } = [];

    public bool Running => State is Phase.Products or Phase.Shortlisting or Phase.Ingredients;

    /// <summary>
    /// There is a shortlist worth showing, complete or not.
    /// </summary>
    /// <remarks>
    /// Asked of the shortlist rather than of the phase, so a run in progress does not count as having
    /// nothing. This used to mean "finished", which made Re-sweep destroy the ranking you pressed it
    /// while reading and give you an empty screen for the several minutes it took to build another.
    /// A shortlist stays valid until a run replaces it, and the prices under it only improve as the
    /// run proceeds.
    /// </remarks>
    public bool HasResults => Shortlist.Count > 0;

    /// <param name="Blocks">How many shortlisted furnishings this one material makes unpriceable.</param>
    public sealed record Blocker(string Material, int Blocks);

    public void Start(string? buying, string? selling, int shortlistSize, TimeSpan maxAge)
    {
        if (Running || string.IsNullOrWhiteSpace(buying) || string.IsNullOrWhiteSpace(selling))
            return;

        State = Phase.Products;
        Detail = "reading the sheets";
        _ = Task.Run(() => Run(buying, selling, shortlistSize, maxAge));
    }

    /// <summary>
    /// Picks up a sweep a previous session finished, without repeating any of its requests.
    /// </summary>
    /// <remarks>
    /// The prices come back from the cache on their own. What is restored here is the far cheaper
    /// half: which furnishings were found worth costing, so the ranking can be rebuilt without
    /// spending another few minutes discovering the same shortlist.
    ///
    /// The sheet walk still happens, on this thread, because a shortlist is a list of ids and the
    /// conversions behind them are not stored.
    /// </remarks>
    public void Restore(StoredSweep stored, string buying, string selling)
    {
        if (Running || HasResults)
            return;

        SellingBoard = selling;
        RestoredBuying = buying;
        RestoredSelling = selling;

        State = Phase.Shortlisting;
        Detail = "restoring the last sweep";

        _ = Task.Run(() =>
        {
            try
            {
                var byId = furnishings.Craftable().ToDictionary(conversion => conversion.Id, StringComparer.Ordinal);

                var shortlist = stored.Shortlist
                    .Select(id => byId.GetValueOrDefault(id))
                    .Where(conversion => conversion is not null)
                    .Select(conversion => conversion!)
                    .ToArray();

                if (shortlist.Length == 0)
                {
                    State = Phase.Idle;
                    Detail = "";
                    return;
                }

                Candidates = stored.Candidates;
                Shortlist = shortlist;
                Blockers = Blocking(shortlist, RestoredBuying, RestoredSelling);
                ReadyAt = DateTimeOffset.FromUnixTimeMilliseconds(stored.At);
                State = Phase.Ready;
                Detail = $"{shortlist.Length} of {Candidates} costed";

                log.Information($"Restored a sweep of {shortlist.Length} furnishings from cache.");
            }
            catch (Exception error)
            {
                State = Phase.Idle;
                Detail = "";
                log.Warning(error, "Could not restore the last sweep.");
            }
        });
    }

    /// <summary>The sweep as it goes to disk. Null until one has finished.</summary>
    public StoredSweep? Snapshot() =>
        HasResults && ReadyAt is { } at
            ? new StoredSweep(at.ToUnixTimeMilliseconds(), Candidates, [.. Shortlist.Select(c => c.Id)])
            : null;

    private async Task Run(string buying, string selling, int shortlistSize, TimeSpan maxAge)
    {
        try
        {
            var candidates = furnishings.Craftable();
            Candidates = candidates.Count;

            if (candidates.Count == 0)
            {
                State = Phase.Failed;
                Detail = "no craftable furnishings found in the sheets";
                return;
            }

            var products = candidates
                .SelectMany(conversion => conversion.Outputs)
                .Select(output => output.Resource.Id)
                .Distinct()
                .ToArray();

            // Surveyed, not priced. Nine hundred items with their listings is a hundred and fifteen
            // requests that mostly time out; nine hundred summarised is nine requests that do not.
            // Depth is only needed for what survives the shortlist.
            //
            // Only what is missing or stale, which is what makes Re-sweep resume rather than start
            // again: a run that lost a hundred products asks for those hundred next time.
            // Surveyed on the selling board. Revenue potential is about demand, and the demand that
            // counts is the one your retainer can actually meet.
            var wanted = products.Where(id => market.SummaryIsStale(selling, id, maxAge)).ToArray();

            State = Phase.Products;
            await Survey(selling, wanted).ConfigureAwait(false);

            // Counted from the cache rather than from the fetch, so data carried over from an
            // earlier run counts exactly as much as what was just fetched.
            var gaps = products.Count(id => market.Summary(selling, id) is null);

            State = Phase.Shortlisting;
            Detail = "picking what is worth costing";
            SellingBoard = selling;
            RestoredBuying = buying;
            RestoredSelling = selling;
            var shortlist = Shortlisted(candidates, shortlistSize);

            if (shortlist.Length == 0)
            {
                Shortlist = [];
                ReadyAt = DateTimeOffset.UtcNow;

                // The distinction that matters. "Nothing is selling" is a finding; "I never got the
                // prices" is a failure, and they must not read the same.
                if (gaps > 0)
                {
                    State = Phase.Partial;
                    Detail = $"no market data for {gaps} of {products.Length} furnishings, so nothing "
                        + "could be ranked. Re-sweep asks only for the gaps.";
                }
                else
                {
                    State = Phase.Ready;
                    Detail = $"none of {Candidates} furnishings are selling on this board";
                }

                return;
            }

            // Full books now, and for the products too: a summary carries no depth, and the profit
            // on a craft is the difference between what the materials really cost and what the
            // product really fetches.
            // Materials on the buying board, products on the selling one: the two halves of a craft
            // happen in different places and pricing them together is what made the old numbers wrong.
            var materialIds = shortlist
                .SelectMany(conversion => conversion.Inputs)
                .Where(amount => amount.Resource.Kind == ResourceKind.Item)
                .Select(amount => amount.Resource.Id)
                .Distinct()
                .Where(id => market.IsStale(buying, id, maxAge))
                .ToArray();

            var productIds = shortlist
                .SelectMany(conversion => conversion.Outputs)
                .Where(amount => amount.Resource.Kind == ResourceKind.Item)
                .Select(amount => amount.Resource.Id)
                .Distinct()
                .Where(id => market.IsStale(selling, id, maxAge))
                .ToArray();

            State = Phase.Ingredients;
            var materials = await Price(buying, materialIds, "materials").ConfigureAwait(false);
            await Price(selling, productIds, "products").ConfigureAwait(false);

            Shortlist = shortlist;
            Blockers = Blocking(shortlist, buying, selling);
            ReadyAt = DateTimeOffset.UtcNow;

            var incomplete = gaps > 0 || materials.FailedChunks > 0;
            State = incomplete ? Phase.Partial : Phase.Ready;

            Detail = incomplete
                ? $"{shortlist.Length} of {Candidates} costed, {gaps} furnishings still unpriced"
                : $"{shortlist.Length} of {Candidates} costed";

            log.Information($"Furnishing sweep done: {shortlist.Length} of {Candidates} costed.");

            foreach (var blocker in Blockers.Take(15))
                log.Information($"  blocked by {blocker.Material} ({blocker.Blocks})");
        }
        catch (Exception error)
        {
            State = Phase.Failed;
            Detail = error.Message;
            log.Error(error, "Furnishing sweep failed.");
        }
    }

    private async Task<MarketCache.PricingResult> Survey(string scope, uint[] ids)
    {
        if (ids.Length == 0)
            return new MarketCache.PricingResult(0, 0, 0);

        Detail = $"surveying {ids.Length} furnishings";

        return await market
            .SurveyAsync(
                scope, ids, FetchPriority.Sweep, (done, total) => Detail = $"surveying: {done} of {total}")
            .ConfigureAwait(false);
    }

    /// <remarks>
    /// Queued rather than attempted, so a press while this runs is served first rather than
    /// dropped. What comes back is what the queue got round to, in its own time.
    /// </remarks>
    private async Task<MarketCache.PricingResult> Price(string scope, uint[] ids, string what)
    {
        if (ids.Length == 0)
            return new MarketCache.PricingResult(0, 0, 0);

        Detail = $"pricing {ids.Length} {what}";

        return await market
            .PriceAsync(
                scope, ids, FetchPriority.Sweep, (done, total) => Detail = $"pricing {what}: {done} of {total}")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The candidates whose product actually turns over, best revenue first.
    /// </summary>
    /// <remarks>
    /// Revenue potential rather than price. A furnishing listed at four million that sells twice
    /// a year is not a business, and ranking on price alone would put it at the top of the queue
    /// for costing while something that quietly moves thirty a day never got looked at.
    /// </remarks>
    /// <summary>
    /// Which candidates are worth the expensive half of the sweep.
    /// </summary>
    /// <remarks>
    /// Mostly the busiest markets, with a slice set aside for the dearest quiet ones. Turnover
    /// is floor times sale rate, so ranking on it alone leans towards things that move, and a
    /// market that turns over little because almost nobody wants it is also one almost nobody
    /// is supplying. The reserved slots are the only way those get costed at all.
    /// </remarks>
    private Conversion[] Shortlisted(IReadOnlyList<Conversion> candidates, int shortlistSize)
    {
        var byId = candidates.ToDictionary(conversion => conversion.Id, StringComparer.Ordinal);

        var picked = CraftShortlist.Pick(
            candidates.Select(conversion => new Candidate(
                conversion.Id,
                Revenue(conversion),
                Cheapest(conversion),
                Pace(conversion))),
            Math.Max(1, shortlistSize),
            Math.Max(0, config.CraftNicheSlots),
            QuietPerDay);

        return [.. picked.Select(id => byId[id])];
    }

    /// <summary>Below this many sales a day, a market is quiet enough to be worth a reserved slot.</summary>
    private const double QuietPerDay = 2d;

    /// <summary>What one of the product sells for, for judging a quiet market's worth.</summary>
    private long Cheapest(Conversion conversion) =>
        conversion.Outputs.Count == 0
            ? 0
            : market.Summary(SellingBoard, conversion.Outputs[0].Resource.Id)?.Floor ?? 0;

    /// <summary>How fast the product moves, for telling a quiet market from a busy one.</summary>
    private double Pace(Conversion conversion) =>
        conversion.Outputs.Count == 0
            ? 0
            : market.Summary(SellingBoard, conversion.Outputs[0].Resource.Id)?.SaleVelocityPerDay ?? 0;

    /// <summary>
    /// Which materials are doing the blocking, counted across the shortlist.
    /// </summary>
    private Blocker[] Blocking(IReadOnlyList<Conversion> shortlist, string buying, string selling) =>
    [
        .. shortlist
            .Select(conversion => ConversionEvaluator.Evaluate(
                conversion, 1, market.Lookup(buying), market.Lookup(selling), MarketTax.Standard))
            .Where(quote => !quote.IsExecutable)
            // Unsourced is a material the board could not supply; Unpriced is one nobody lists at
            // all. Both stop a row being costed, and for this question they count the same.
            .SelectMany(quote => quote.Unsourced.Concat(quote.Unpriced))
            .GroupBy(amount => amount.Resource.Name)
            .Select(group => new Blocker(group.Key, group.Count()))
            .OrderByDescending(blocker => blocker.Blocks),
    ];

    /// <summary>The board a restore or a run last used for selling, for recomputing derived figures.</summary>
    private string SellingBoard { get; set; } = "";

    private string RestoredBuying { get; set; } = "";

    private string RestoredSelling { get; set; } = "";

    private double Revenue(Conversion conversion)
    {
        double total = 0;

        foreach (var output in conversion.Outputs)
        {
            if (market.Summary(SellingBoard, output.Resource.Id) is { } summary)
                total += summary.DailyRevenue * output.Quantity;
        }

        return total;
    }
}

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
internal sealed class CraftSweep(
    Craftables furnishings,
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

    /// <summary>
    /// Everything a reader needs about the sweep, as one value.
    /// </summary>
    /// <remarks>
    /// One record swapped whole rather than six properties written one after another. They were
    /// written from a background task and read from the draw thread, and while each write is
    /// atomic on its own, nothing made them atomic together: the phase turning to Ready one
    /// instruction before the shortlist was stored is a frame that draws a finished sweep with
    /// the previous run's rows in it. That is the kind of fault that happens once and never
    /// reproduces, so it is designed out rather than watched for.
    ///
    /// The same shape the gathering sweep already uses, and the same shape the views use for
    /// their own snapshots.
    /// </remarks>
    /// <param name="Detail">Where the sweep has got to, for showing rather than for logic.</param>
    /// <param name="Candidates">Every craftable sellable thing found in the sheets.</param>
    /// <param name="Shortlist">The ones worth costing, once the products were priced.</param>
    /// <param name="Blockers">What stands between the shortlist and a price, commonest first.</param>
    public sealed record Snapshot(
        Phase State,
        string Detail,
        int Candidates,
        IReadOnlyList<Conversion> Shortlist,
        DateTimeOffset? ReadyAt,
        IReadOnlyList<Blocker> Blockers)
    {
        public bool Running => State is Phase.Products or Phase.Shortlisting or Phase.Ingredients;

        /// <summary>
        /// There is a shortlist worth showing, complete or not.
        /// </summary>
        /// <remarks>
        /// Asked of the shortlist rather than of the phase, so a run in progress does not count as
        /// having nothing. This used to mean "finished", which made Re-sweep destroy the ranking you
        /// pressed it while reading and give you an empty screen for the several minutes it took to
        /// build another. A shortlist stays valid until a run replaces it, and the prices under it
        /// only improve as the run proceeds.
        /// </remarks>
        public bool HasResults => Shortlist.Count > 0;
    }

    /// <summary>The sweep as it stands. Read it once; it is replaced whole, never edited.</summary>
    public Snapshot Current { get; private set; } = new(Phase.Idle, "", 0, [], null, []);

    /// <param name="Blocks">How many shortlisted crafts this one material makes unpriceable.</param>
    public sealed record Blocker(string Material, int Blocks);

    public void Start(string? buying, string? selling, int shortlistSize, TimeSpan maxAge)
    {
        if (Current.Running || string.IsNullOrWhiteSpace(buying) || string.IsNullOrWhiteSpace(selling))
            return;

        Current = Current with { State = Phase.Products, Detail = "reading the sheets" };
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
        if (Current.Running || Current.HasResults)
            return;

        against = new Against(buying, selling);
        Current = Current with { State = Phase.Shortlisting, Detail = "restoring the last sweep" };

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
                    Current = Current with { State = Phase.Idle, Detail = "" };
                    return;
                }

                Current = new Snapshot(
                    Phase.Ready,
                    $"{shortlist.Length} of {stored.Candidates} costed",
                    stored.Candidates,
                    shortlist,
                    DateTimeOffset.FromUnixTimeMilliseconds(stored.At),
                    Blocking(shortlist, against.Buying, against.Selling));

                log.Information($"Restored a sweep of {shortlist.Length} crafts from cache.");
            }
            catch (Exception error)
            {
                Current = Current with { State = Phase.Idle, Detail = "" };
                log.Warning(error, "Could not restore the last sweep.");
            }
        });
    }

    /// <summary>The sweep as it goes to disk. Null until one has finished.</summary>
    public StoredSweep? Stored()
    {
        var now = Current;

        return now.HasResults && now.ReadyAt is { } at
            ? new StoredSweep(at.ToUnixTimeMilliseconds(), now.Candidates, [.. now.Shortlist.Select(c => c.Id)])
            : null;
    }

    private async Task Run(string buying, string selling, int shortlistSize, TimeSpan maxAge)
    {
        try
        {
            var candidates = furnishings.Craftable();

            Current = Current with { Candidates = candidates.Count };

            if (candidates.Count == 0)
            {
                Current = Current with
                {
                    State = Phase.Failed,
                    Detail = "no craftable, sellable things found in the sheets",
                };

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

            Current = Current with { State = Phase.Products };
            await Survey(selling, wanted).ConfigureAwait(false);

            // Counted from the cache rather than from the fetch, so data carried over from an
            // earlier run counts exactly as much as what was just fetched.
            var gaps = products.Count(id => market.Summary(selling, id) is null);

            Current = Current with { State = Phase.Shortlisting, Detail = "picking what is worth costing" };
            against = new Against(buying, selling);

            var shortlist = Shortlisted(candidates, shortlistSize);

            if (shortlist.Length == 0)
            {
                // The distinction that matters. "Nothing is selling" is a finding; "I never got the
                // prices" is a failure, and they must not read the same.
                Current = Current with
                {
                    State = gaps > 0 ? Phase.Partial : Phase.Ready,
                    Detail = gaps > 0
                        ? $"no market data for {gaps} of {products.Length} things, so nothing could be "
                          + "ranked. Re-sweep asks only for the gaps."
                        : $"none of {Current.Candidates} are selling on this board",
                    Shortlist = [],
                    ReadyAt = DateTimeOffset.UtcNow,
                };

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

            Current = Current with { State = Phase.Ingredients };

            var materials = await Price(buying, materialIds, "materials").ConfigureAwait(false);
            await Price(selling, productIds, "products").ConfigureAwait(false);

            var blockers = Blocking(shortlist, buying, selling);
            var incomplete = gaps > 0 || materials.FailedChunks > 0;
            var costed = $"{shortlist.Length} of {Current.Candidates} costed";

            // Everything the finished run has to say, published in one write. A reader either
            // sees the whole of this run or the whole of the last one, never half of each.
            Current = Current with
            {
                State = incomplete ? Phase.Partial : Phase.Ready,
                Detail = incomplete ? $"{costed}, {gaps} still unpriced" : costed,
                Shortlist = shortlist,
                ReadyAt = DateTimeOffset.UtcNow,
                Blockers = blockers,
            };

            log.Information($"Craft sweep done: {costed}.");

            foreach (var blocker in blockers.Take(15))
                log.Information($"  blocked by {blocker.Material} ({blocker.Blocks})");
        }
        catch (Exception error)
        {
            Current = Current with { State = Phase.Failed, Detail = error.Message };
            log.Error(error, "Craft sweep failed.");
        }
    }

    private async Task<MarketCache.PricingResult> Survey(string scope, uint[] ids)
    {
        if (ids.Length == 0)
            return new MarketCache.PricingResult(0, 0, 0);

        Current = Current with { Detail = $"surveying {ids.Length} things" };

        return await market
            .SurveyAsync(
                scope, ids, FetchPriority.Sweep, (done, total) => Current = Current with { Detail = $"surveying: {done} of {total}" })
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

        Current = Current with { Detail = $"pricing {ids.Length} {what}" };

        return await market
            .PriceAsync(
                scope, ids, FetchPriority.Sweep, (done, total) => Current = Current with { Detail = $"pricing {what}: {done} of {total}" })
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
            : market.Summary(against.Selling, conversion.Outputs[0].Resource.Id)?.Floor ?? 0;

    /// <summary>How fast the product moves, for telling a quiet market from a busy one.</summary>
    private double Pace(Conversion conversion) =>
        conversion.Outputs.Count == 0
            ? 0
            : market.Summary(against.Selling, conversion.Outputs[0].Resource.Id)?.SaleVelocityPerDay ?? 0;

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
    /// <summary>Which boards the shortlist was priced against: one job, not three properties.</summary>
    private sealed record Against(string Buying, string Selling);

    private Against against = new("", "");

    
    
    private double Revenue(Conversion conversion)
    {
        double total = 0;

        foreach (var output in conversion.Outputs)
        {
            if (market.Summary(against.Selling, output.Resource.Id) is { } summary)
                total += summary.DailyRevenue * output.Quantity;
        }

        return total;
    }
}

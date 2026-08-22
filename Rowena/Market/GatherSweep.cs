using Dalamud.Plugin.Services;
using Rowena.Core.Market;
using Rowena.Game;

namespace Rowena.Market;

/// <summary>
/// Works out which gatherable things are worth the walk, cheaply.
/// </summary>
/// <remarks>
/// The same two passes as everything else here, and by far the cheapest of them: seven
/// hundred and seventy marketable gatherables is eight summary requests, and only the
/// handful worth ranking need their books. A furnishing sweep is minutes; this is seconds.
///
/// Surveyed on the selling board rather than the buying one. Nothing here is bought: the
/// question is what your own retainer would get for it, and that is a fact about your world
/// rather than about the cheapest world on the data centre.
/// </remarks>
internal sealed class GatherSweep(Gatherables gatherables, MarketCache market, Diagnostics diagnostics, IPluginLog log)
{
    public enum Phase
    {
        Idle,
        Surveying,
        Pricing,
        Ready,
        Failed,
    }

    /// <param name="Shortlist">The items worth ranking, with their books fetched.</param>
    public sealed record Snapshot(
        Phase State,
        string Detail,
        int Candidates,
        IReadOnlyList<uint> Shortlist,
        DateTimeOffset? ReadyAt)
    {
        public bool Running => State is Phase.Surveying or Phase.Pricing;

        public bool HasResults => Shortlist.Count > 0;
    }

    public Snapshot Current { get; private set; } = new(Phase.Idle, "", 0, [], null);

    /// <summary>Surveys everything gatherable, then costs the best of it.</summary>
    public void Start(string? selling, int shortlistSize, TimeSpan maxAge)
    {
        if (Current.Running || string.IsNullOrWhiteSpace(selling))
            return;

        Current = Current with { State = Phase.Surveying, Detail = "reading the gathering sheets" };
        _ = Task.Run(() => Run(selling, shortlistSize, maxAge));
    }

    /// <summary>
    /// Rebuilds the shortlist from what a previous session already surveyed.
    /// </summary>
    public void RestoreOnce(string selling, int shortlistSize)
    {
        if (Current.Running || Current.HasResults || Current.State != Phase.Idle)
            return;

        Current = Current with { State = Phase.Pricing, Detail = "reading what the cache already knows" };

        _ = Task.Run(() =>
        {
            try
            {
                var candidates = gatherables.All();
                var shortlist = Shortlisted(selling, candidates, shortlistSize, out var surveyed);

                Current = surveyed == 0
                    ? new Snapshot(Phase.Idle, "", 0, [], null)
                    : new Snapshot(
                        Phase.Ready,
                        $"{shortlist.Count} worth ranking, from {surveyed} of {candidates.Count} the cache knew",
                        candidates.Count,
                        shortlist,
                        market.LastRefresh);

                diagnostics.Note("gather", $"restored {shortlist.Count} from {surveyed} surveyed");
            }
            catch (Exception error)
            {
                Current = new Snapshot(Phase.Idle, "", 0, [], null);
                log.Warning(error, "Could not rebuild the gathering shortlist.");
            }
        });
    }

    private async Task Run(string selling, int shortlistSize, TimeSpan maxAge)
    {
        try
        {
            var candidates = gatherables.All();

            if (candidates.Count == 0)
            {
                Current = Current with { State = Phase.Failed, Detail = "nothing gatherable found in the sheets" };
                return;
            }

            var wanted = candidates
                .Select(gatherable => gatherable.ItemId)
                .Where(id => market.SummaryIsStale(selling, id, maxAge))
                .ToArray();

            Current = Current with
            {
                Candidates = candidates.Count,
                Detail = $"surveying {wanted.Length} of {candidates.Count}",
            };

            if (wanted.Length > 0)
            {
                await market
                    .SurveyAsync(
                        selling, wanted, FetchPriority.Sweep,
                        (done, total) => Current = Current with { Detail = $"surveying: {done} of {total}" })
                    .ConfigureAwait(false);
            }

            var shortlist = Shortlisted(selling, candidates, shortlistSize, out var surveyed);

            // Books for the shortlist, because a summary carries no depth and no history, and
            // without history a listing at a silly price is taken at face value.
            var stale = shortlist.Where(id => market.IsStale(selling, id, maxAge)).ToArray();

            if (stale.Length > 0)
            {
                Current = Current with { State = Phase.Pricing, Detail = $"pricing {stale.Length}" };

                await market
                    .PriceAsync(
                        selling, stale, FetchPriority.Sweep,
                        (done, total) => Current = Current with { Detail = $"pricing: {done} of {total}" })
                    .ConfigureAwait(false);
            }

            Current = new Snapshot(
                Phase.Ready,
                $"{shortlist.Count} worth ranking, of {surveyed} surveyed",
                candidates.Count,
                shortlist,
                DateTimeOffset.UtcNow);

            log.Information($"Gathering sweep done: {shortlist.Count} of {candidates.Count}.");
        }
        catch (Exception error)
        {
            Current = Current with { State = Phase.Failed, Detail = error.Message };
            log.Error(error, "Gathering sweep failed.");
        }
    }

    /// <summary>
    /// The gatherables whose market actually turns over, best first.
    /// </summary>
    /// <remarks>
    /// Ranked on what the whole board turns over in a day rather than on price. A crystal
    /// worth ninety gil that moves four thousand a day is a better hour than a rock worth
    /// forty thousand that sells twice a week, and ranking on price alone would never show it.
    /// </remarks>
    private IReadOnlyList<uint> Shortlisted(
        string selling,
        IReadOnlyList<Gatherable> candidates,
        int shortlistSize,
        out int surveyed)
    {
        var seen = 0;
        var ranked = new List<(uint Id, double Revenue)>();

        foreach (var gatherable in candidates)
        {
            if (market.Summary(selling, gatherable.ItemId) is not { } summary)
                continue;

            seen++;

            if (summary.DailyRevenue > 0)
                ranked.Add((gatherable.ItemId, summary.DailyRevenue));
        }

        surveyed = seen;

        return
        [
            .. ranked
                .OrderByDescending(entry => entry.Revenue)
                .Take(Math.Max(1, shortlistSize))
                .Select(entry => entry.Id),
        ];
    }
}

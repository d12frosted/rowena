using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using Rowena.Core.Market;
using Rowena.Core.Universalis;

namespace Rowena.Market;

/// <summary>
/// Holds what the board said, so the window can be drawn every frame without hammering a free
/// service.
/// </summary>
/// <remarks>
/// Universalis is crowdsourced and costs nobody anything to use, which is exactly why it deserves
/// to be used politely: batched, cached, and paced.
///
/// There are two ways in and the difference is not small. Measured against Light, a hundred items
/// summarised comes back in under two seconds, while ten items with their listings times out at the
/// gateway's ten-second limit. An earlier reading of twenty succeeded only because it landed at
/// 8.4 seconds, right on the edge. So anything large is surveyed first and only the survivors are
/// asked for in full.
/// </remarks>
internal sealed class MarketCache(IMarketDataSource source, PriceStore store, IPluginLog log)
{
    /// <summary>
    /// Three tries, because a 504 says the service is struggling rather than that the request was
    /// wrong, and struggling usually passes.
    /// </summary>
    private const int Attempts = 3;

    private static readonly TimeSpan BetweenChunks = TimeSpan.FromMilliseconds(300);

    /// <summary>Waits between attempts at the same chunk, lengthening each time.</summary>
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)];

    /// <summary>How far the gap between chunks may stretch once things start failing.</summary>
    private static readonly TimeSpan SlowestBetweenChunks = TimeSpan.FromSeconds(3);

    // Keyed by board as well as item, because the two sides of a trade happen on different ones and
    // an item can be an input to one conversion and an output of another.
    private readonly ConcurrentDictionary<(string Scope, uint ItemId), BookSnapshot> books = new();
    private readonly ConcurrentDictionary<(string Scope, uint ItemId), SummarySnapshot> summaries = new();

    private bool restored;

    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>True while any fetch is in flight, so nothing starts a second one.</summary>
    public bool Busy { get; private set; }

    public DateTimeOffset? LastRefresh { get; private set; }

    /// <summary>The last failure, kept so the window can say so instead of showing nothing.</summary>
    public string? LastError { get; private set; }

    public OrderBook? Book(string scope, uint itemId) =>
        books.TryGetValue((scope, itemId), out var snapshot) ? snapshot.Book : null;

    /// <summary>A lookup shaped for the evaluator, bound to one board.</summary>
    public Func<uint, OrderBook?> Lookup(string scope) => itemId => Book(scope, itemId);

    /// <summary>The cheap answer, when one has been fetched.</summary>
    public MarketSummary? Summary(string scope, uint itemId) =>
        summaries.TryGetValue((scope, itemId), out var snapshot) ? snapshot.Summary : null;

    public bool IsStale(string scope, uint itemId) => IsStale(scope, itemId, Ttl);

    /// <summary>
    /// Whether an item's book is older than the caller is willing to accept.
    /// </summary>
    /// <remarks>
    /// The age is the caller's business rather than one setting, because two questions here have
    /// completely different needs. Deciding whether to spend fifteen million on tokens wants depth
    /// from minutes ago. Deciding which of nine hundred furnishings is worth making wants a rough
    /// map, and one from this morning is fine, which is what makes a sweep affordable.
    /// </remarks>
    public bool IsStale(string scope, uint itemId, TimeSpan maxAge) =>
        !books.TryGetValue((scope, itemId), out var snapshot)
        || DateTimeOffset.UtcNow - snapshot.Fetched > maxAge;

    public bool SummaryIsStale(string scope, uint itemId, TimeSpan maxAge) =>
        !summaries.TryGetValue((scope, itemId), out var snapshot)
        || DateTimeOffset.UtcNow - snapshot.Fetched > maxAge;

    /// <param name="Answered">Ids that came back with something, an empty answer included.</param>
    /// <param name="FailedChunks">Batches given up on, each one a hole in the data.</param>
    public readonly record struct PricingResult(int Requested, int Answered, int FailedChunks)
    {
        public double Coverage => Requested == 0 ? 1d : (double)Answered / Requested;
    }

    /// <summary>
    /// Fetches anything missing or past its shelf life. Returns immediately; the window keeps
    /// drawing whatever it already had.
    /// </summary>
    /// <param name="scope">
    /// Where to price against, resolved by the caller. It has to arrive already answered: this
    /// starts a background task, and working it out in there would mean reading game state off the
    /// framework thread, which throws.
    /// </param>
    public void RefreshInBackground(string? scope, IReadOnlyCollection<uint> itemIds, bool force = false)
    {
        if (Busy)
            return;

        if (string.IsNullOrWhiteSpace(scope))
        {
            LastError = "Not logged in, and no data centre set.";
            return;
        }

        var wanted = force ? [.. itemIds] : itemIds.Where(id => IsStale(scope, id)).ToArray();
        if (wanted.Length == 0)
            return;

        _ = Task.Run(() => PriceAsync(scope, wanted, chunkSize: 8));
    }

    /// <summary>Fetches full books, with their depth, in small batches.</summary>
    public Task<PricingResult> PriceAsync(
        string scope,
        IReadOnlyList<uint> itemIds,
        int chunkSize,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default) =>
        Batched(
            itemIds,
            chunkSize,
            async (chunk, token) =>
                StoreBooks(scope, await source.FetchAsync(scope, chunk, token).ConfigureAwait(false), chunk),
            onProgress,
            cancellationToken);

    /// <summary>Fetches prices and sale rates only, in large batches.</summary>
    public Task<PricingResult> SurveyAsync(
        string scope,
        IReadOnlyList<uint> itemIds,
        int chunkSize,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default) =>
        Batched(
            itemIds,
            chunkSize,
            async (chunk, token) =>
                StoreSummaries(scope, await source.SurveyAsync(scope, chunk, token).ConfigureAwait(false), chunk),
            onProgress,
            cancellationToken);

    /// <summary>
    /// Walks a list in batches, retrying each and easing off as failures accumulate.
    /// </summary>
    /// <remarks>
    /// A batch that fails every attempt is logged and skipped rather than failing the run. Losing
    /// eight prices is a gap in one table; abandoning the run loses the other nine hundred too.
    ///
    /// But skipping quietly is its own trap: a run that lost most of its batches looks exactly like
    /// one that found nothing for sale, and that is a confident wrong answer. So the count comes
    /// back with the result and the caller is expected to care.
    ///
    /// The gap between batches stretches as failures mount. A service returning 504s is asking to be
    /// left alone for a moment, and carrying on at the same rate is how a bad minute becomes a
    /// failed sweep.
    /// </remarks>
    private async Task<PricingResult> Batched(
        IReadOnlyList<uint> itemIds,
        int chunkSize,
        Func<uint[], CancellationToken, Task<bool>> fetch,
        Action<int, int>? onProgress,
        CancellationToken cancellationToken)
    {
        if (Busy || itemIds.Count == 0)
            return new PricingResult(itemIds.Count, 0, 0);

        Busy = true;
        var answered = 0;
        var seen = 0;
        var failedChunks = 0;

        try
        {
            foreach (var chunk in itemIds.Chunk(Math.Max(1, chunkSize)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await Attempt(chunk, fetch, cancellationToken).ConfigureAwait(false))
                    answered += chunk.Length;
                else
                    failedChunks++;

                seen += chunk.Length;
                onProgress?.Invoke(seen, itemIds.Count);

                if (seen < itemIds.Count)
                {
                    var pause = Math.Min(
                        SlowestBetweenChunks.TotalMilliseconds,
                        BetweenChunks.TotalMilliseconds * (1 + failedChunks));

                    await Task.Delay(TimeSpan.FromMilliseconds(pause), cancellationToken).ConfigureAwait(false);
                }
            }

            LastRefresh = DateTimeOffset.UtcNow;

            if (answered > 0)
                LastError = null;
        }
        catch (OperationCanceledException)
        {
            // A cancelled run is not a failure worth reporting to the user.
        }
        catch (Exception error)
        {
            LastError = error.Message;
            log.Error(error, "Could not fetch market data.");
        }
        finally
        {
            Busy = false;
        }

        if (failedChunks > 0)
            log.Warning($"Got {answered} of {itemIds.Count} ids; {failedChunks} batches were given up on.");

        return new PricingResult(itemIds.Count, answered, failedChunks);
    }

    private async Task<bool> Attempt(
        uint[] chunk,
        Func<uint[], CancellationToken, Task<bool>> fetch,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                return await fetch(chunk, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error) when (attempt < Attempts)
            {
                log.Verbose(error, $"Retrying a batch of {chunk.Length} (attempt {attempt}).");
                await Task.Delay(Backoff[attempt - 1], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                LastError = error.Message;
                log.Warning(error, $"Gave up on a batch of {chunk.Length}; that data will be missing.");
            }
        }

        return false;
    }

    /// <summary>
    /// Records what came back, and an empty book for anything that did not.
    /// </summary>
    /// <remarks>
    /// Universalis omits items it has nothing for, mostly the untradable ones. Storing an empty book
    /// for those keeps "asked, and there is nothing listed" apart from "never asked", and stops
    /// every refresh requesting them again forever.
    /// </remarks>
    private bool StoreBooks(
        string scope,
        IReadOnlyDictionary<uint, OrderBook> fetched,
        IReadOnlyCollection<uint> requested)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var itemId in requested)
        {
            var book = fetched.TryGetValue(itemId, out var found) ? found : OrderBook.Empty(itemId);
            books[(scope, itemId)] = new BookSnapshot(WithSurveyedVelocity(scope, book), now);
        }

        return true;
    }

    private bool StoreSummaries(
        string scope,
        IReadOnlyDictionary<uint, MarketSummary> fetched,
        IReadOnlyCollection<uint> requested)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var itemId in requested)
        {
            var summary = fetched.TryGetValue(itemId, out var found)
                ? found
                : new MarketSummary(itemId, null, 0d);

            summaries[(scope, itemId)] = new SummarySnapshot(summary, now);

            // A book already held was fetched with the other endpoint's sale rate. Bring it into
            // line rather than leaving two numbers in play.
            if (books.TryGetValue((scope, itemId), out var existing))
            {
                books[(scope, itemId)] =
                    existing with { Book = existing.Book.WithVelocity(summary.SaleVelocityPerDay) };
            }
        }

        return true;
    }

    /// <summary>
    /// Imposes the surveyed sale rate on a book, when one is known.
    /// </summary>
    /// <remarks>
    /// The two endpoints disagree, sometimes by more than threefold. Whichever is closer to the
    /// truth, one of them has to win everywhere, or an item gets shortlisted on one number and
    /// ranked on another. The survey wins because it is the one every candidate has.
    /// </remarks>
    private OrderBook WithSurveyedVelocity(string scope, OrderBook book) =>
        summaries.TryGetValue((scope, book.ItemId), out var summary)
            ? book.WithVelocity(summary.Summary.SaleVelocityPerDay)
            : book;

    /// <summary>Everything held, for writing to disk.</summary>
    public IEnumerable<(string Scope, OrderBook Book, DateTimeOffset Fetched)> ExportBooks() =>
        books.Select(entry => (entry.Key.Scope, entry.Value.Book, entry.Value.Fetched));

    public IEnumerable<(string Scope, MarketSummary Summary, DateTimeOffset Fetched)> ExportSummaries() =>
        summaries.Select(entry => (entry.Key.Scope, entry.Value.Summary, entry.Value.Fetched));

    /// <summary>What a previous session swept, if it is still worth having.</summary>
    public StoredSweep? RestoredSweep { get; private set; }

    /// <summary>
    /// Loads saved data, once, as soon as the scope is known.
    /// </summary>
    /// <remarks>
    /// Not in the constructor, because which board we are pricing against is only knowable after a
    /// character is loaded, and prices from the wrong one are worse than none.
    /// </remarks>
    public void RestoreOnce(TimeSpan maxAge)
    {
        if (restored)
            return;

        restored = true;

        if (store.Load(maxAge) is not { } loaded)
            return;

        // Summaries first, so the books that follow are stamped with the winning sale rate.
        foreach (var (scope, summary, fetched) in loaded.Summaries)
            summaries[(scope, summary.ItemId)] = new SummarySnapshot(summary, fetched);

        foreach (var (scope, book, fetched) in loaded.Books)
            books[(scope, book.ItemId)] = new BookSnapshot(WithSurveyedVelocity(scope, book), fetched);

        RestoredSweep = loaded.Sweep;
    }

    public void Persist(StoredSweep? sweep)
    {
        if (!books.IsEmpty || !summaries.IsEmpty)
            store.Save(ExportBooks(), ExportSummaries(), sweep);
    }

    private readonly record struct BookSnapshot(OrderBook Book, DateTimeOffset Fetched);

    private readonly record struct SummarySnapshot(MarketSummary Summary, DateTimeOffset Fetched);
}

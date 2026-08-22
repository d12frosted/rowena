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
///
/// Everything wanted goes through one queue and one worker. There is one fetcher because a plugin
/// that opens ten connections to a free service is not being reasonable, and one fetcher means the
/// order matters: a scan is thousands of ids that can wait, a press is two that cannot. Work used to
/// be attempted rather than queued, and whoever asked second was dropped without a word, which is
/// how pressing refresh during a vendor scan came to do nothing at all.
/// </remarks>
internal sealed class MarketCache : IDisposable
{
    private readonly IMarketDataSource source;
    private readonly PriceStore store;
    private readonly IPluginLog log;
    private readonly Diagnostics diagnostics;

    private readonly FetchQueue queue = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly List<Request> requests = [];
    private readonly object waiting = new();

    private volatile bool inFlight;
    private int flying;
    private int done;

    public MarketCache(IMarketDataSource source, PriceStore store, Diagnostics diagnostics, IPluginLog log)
    {
        this.source = source;
        this.store = store;
        this.diagnostics = diagnostics;
        this.log = log;

        _ = Task.Run(() => Work(stopping.Token));
    }

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

    /// <summary>Ids per request when the whole book is wanted. Small: the gateway times out.</summary>
    public int BookBatchSize { get; set; } = 8;

    /// <summary>Ids per request when only the price is wanted. Large: a summary is cheap.</summary>
    public int SummaryBatchSize { get; set; } = 100;

    /// <summary>True while anything is queued or in flight.</summary>
    public bool Busy => inFlight || queue.Pending > 0;

    /// <summary>
    /// How far the queue has got, as ids answered out of ids asked. Null when idle.
    /// </summary>
    /// <remarks>
    /// Counted across everything waiting rather than per caller, because that is the honest
    /// answer to "how long until this stops": a press queued behind a sweep is waiting for the
    /// sweep too. "Fetching..." with no end in sight reads as stuck after ten seconds, and a
    /// scan can legitimately take minutes.
    /// </remarks>
    public (int Done, int Total)? Progress
    {
        get
        {
            // What is queued plus what is being asked for right now. Leaving the batch in
            // flight out of it meant a single-item fetch reported "0 of 0", since the queue
            // empties the moment the batch is taken.
            var left = queue.Pending + Volatile.Read(ref flying);

            if (left == 0)
                return null;

            // Counted rather than remembered: the total is what has been done plus what is
            // still outstanding, so it grows when more is asked for and cannot drift out of
            // step with a queue that merges duplicates.
            var finished = Volatile.Read(ref done);
            return (finished, finished + left);
        }
    }

    /// <summary>How many ids are queued at each urgency, for saying what is being waited on.</summary>
    public int PendingAt(FetchPriority priority) => queue.PendingAt(priority);

    /// <summary>How many books and summaries are held, for saying whether anything landed.</summary>
    public (int Books, int Summaries) Held => (books.Count, summaries.Count);

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
    public void RefreshInBackground(
        string? scope,
        IReadOnlyCollection<uint> itemIds,
        bool force = false,
        FetchPriority priority = FetchPriority.Background)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            LastError = "Not logged in, and no data centre set.";
            return;
        }

        var wanted = force ? [.. itemIds] : itemIds.Where(id => IsStale(scope, id)).ToArray();
        if (wanted.Length == 0)
            return;

        Submit(scope, FetchKind.Book, wanted, priority, null);
    }

    /// <summary>Queues full books, with their depth, and waits for them.</summary>
    public Task<PricingResult> PriceAsync(
        string scope,
        IReadOnlyList<uint> itemIds,
        FetchPriority priority = FetchPriority.Background,
        Action<int, int>? onProgress = null) =>
        Submit(scope, FetchKind.Book, itemIds, priority, onProgress);

    /// <summary>Queues prices and sale rates only, and waits for them.</summary>
    public Task<PricingResult> SurveyAsync(
        string scope,
        IReadOnlyList<uint> itemIds,
        FetchPriority priority = FetchPriority.Background,
        Action<int, int>? onProgress = null) =>
        Submit(scope, FetchKind.Summary, itemIds, priority, onProgress);

    /// <summary>
    /// Puts work on the queue and hands back something to wait on.
    /// </summary>
    /// <remarks>
    /// The waiting is per caller and the queue is shared, so two callers wanting the same id wait
    /// on one fetch. A caller is finished when every id it asked for has been attempted, whoever
    /// it was attempted for.
    /// </remarks>
    private Task<PricingResult> Submit(
        string scope,
        FetchKind kind,
        IReadOnlyCollection<uint> itemIds,
        FetchPriority priority,
        Action<int, int>? onProgress)
    {
        var wanted = itemIds.Distinct().ToArray();

        if (wanted.Length == 0)
            return Task.FromResult(new PricingResult(0, 0, 0));

        var request = new Request(
            scope,
            kind,
            [.. wanted],
            wanted.Length,
            onProgress);

        lock (waiting)
            requests.Add(request);

        queue.Enqueue(scope, kind, wanted, priority);
        diagnostics.Note("fetch", $"queued {wanted.Length} {kind} on {scope} at {priority}");

        return request.Completion.Task;
    }

    /// <summary>
    /// The one fetcher, taking whatever is most urgent and easing off as failures accumulate.
    /// </summary>
    /// <remarks>
    /// A batch that fails every attempt is logged and skipped rather than failing anything. Losing
    /// eight prices is a gap in one table; abandoning the run loses the other nine hundred too.
    ///
    /// But skipping quietly is its own trap: a run that lost most of its batches looks exactly like
    /// one that found nothing for sale, and that is a confident wrong answer. So the count comes
    /// back to whoever was waiting and the caller is expected to care.
    ///
    /// The gap between batches stretches as failures mount. A service returning 504s is asking to be
    /// left alone for a moment, and carrying on at the same rate is how a bad minute becomes a
    /// failed sweep. It relaxes again as batches start landing, since the alternative is one bad
    /// patch slowing the rest of the session.
    /// </remarks>
    private async Task Work(CancellationToken cancellationToken)
    {
        var failures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (queue.Next(SizeFor) is not { } batch)
            {
                // Nothing to do: the counters go back to zero so the next run reads from zero
                // rather than continuing somebody else's total.
                if (!inFlight)
                    Interlocked.Exchange(ref done, 0);

                await Idle(cancellationToken).ConfigureAwait(false);
                continue;
            }

            inFlight = true;
            Interlocked.Exchange(ref flying, batch.Ids.Length);

            try
            {
                var answered = await Attempt(batch, cancellationToken).ConfigureAwait(false);

                failures = answered ? Math.Max(0, failures - 1) : failures + 1;

                if (answered)
                {
                    LastRefresh = DateTimeOffset.UtcNow;
                    LastError = null;
                }

                Interlocked.Add(ref done, batch.Ids.Length);

                diagnostics.Note(
                    "fetch",
                    $"{(answered ? "got" : "gave up on")} {batch.Ids.Length} {batch.Kind} on {batch.Scope} "
                    + $"({batch.Priority}), {queue.Pending} left");

                Finish(batch, answered);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error)
            {
                LastError = error.Message;
                log.Error(error, "Could not fetch market data.");
                Finish(batch, answered: false);
            }
            finally
            {
                Interlocked.Exchange(ref flying, 0);
                inFlight = false;
            }

            var pause = Math.Min(
                SlowestBetweenChunks.TotalMilliseconds,
                BetweenChunks.TotalMilliseconds * (1 + failures));

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(pause), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private int SizeFor(FetchKind kind) =>
        kind == FetchKind.Book ? Math.Max(1, BookBatchSize) : Math.Max(1, SummaryBatchSize);

    private static async Task Idle(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Tells everyone waiting on these ids that they have been attempted.</summary>
    private void Finish(FetchBatch batch, bool answered)
    {
        List<Request> finished = [];

        lock (waiting)
        {
            foreach (var request in requests)
            {
                if (request.Kind != batch.Kind || !string.Equals(request.Scope, batch.Scope, StringComparison.Ordinal))
                    continue;

                var hit = 0;

                foreach (var itemId in batch.Ids)
                {
                    if (request.Outstanding.Remove(itemId))
                        hit++;
                }

                if (hit == 0)
                    continue;

                if (answered)
                    request.Answered += hit;
                else
                    request.FailedChunks++;

                request.OnProgress?.Invoke(request.Total - request.Outstanding.Count, request.Total);

                if (request.Outstanding.Count == 0)
                    finished.Add(request);
            }

            foreach (var request in finished)
                requests.Remove(request);
        }

        foreach (var request in finished)
        {
            if (request.FailedChunks > 0)
            {
                log.Warning(
                    $"Got {request.Answered} of {request.Total} ids; "
                    + $"{request.FailedChunks} batches were given up on.");
            }

            request.Completion.TrySetResult(
                new PricingResult(request.Total, request.Answered, request.FailedChunks));
        }
    }

    private async Task<bool> Attempt(FetchBatch batch, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                return batch.Kind == FetchKind.Book
                    ? StoreBooks(
                        batch.Scope,
                        await source.FetchAsync(batch.Scope, batch.Ids, cancellationToken).ConfigureAwait(false),
                        batch.Ids)
                    : StoreSummaries(
                        batch.Scope,
                        await source.SurveyAsync(batch.Scope, batch.Ids, cancellationToken).ConfigureAwait(false),
                        batch.Ids);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error) when (attempt < Attempts)
            {
                log.Verbose(error, $"Retrying a batch of {batch.Ids.Length} (attempt {attempt}).");
                await Task.Delay(Backoff[attempt - 1], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                LastError = error.Message;
                log.Warning(error, $"Gave up on a batch of {batch.Ids.Length}; that data will be missing.");
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

        var withHistory = 0;

        foreach (var itemId in requested)
        {
            var book = fetched.TryGetValue(itemId, out var found) ? found : OrderBook.Empty(itemId);

            if (book.RecentSales.Count > 0)
                withHistory++;

            books[(scope, itemId)] = new BookSnapshot(WithSurveyedVelocity(scope, book), now);
        }

        diagnostics.Note("fetch", $"stored {requested.Count} books on {scope}, {withHistory} with sale history");

        // After the writes, so a handler reading the cache sees what just landed. One handler
        // throwing is its own problem and must not lose the rest of the batch or kill the
        // worker that fetched it.
        foreach (var itemId in requested)
        {
            try
            {
                BookChanged?.Invoke(scope, itemId);
            }
            catch (Exception error)
            {
                log.Warning(error, $"A handler failed on {itemId} changing.");
            }
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
    /// <summary>
    /// Raised for each book replaced by a fetch, with the board it is on.
    /// </summary>
    /// <remarks>
    /// So that anything caring about one item can recompute that item rather than everything
    /// re-reading the whole cache on a timer. A timer scraping the cache every few seconds
    /// finds an undercut on its next tick at best, and finds a vendor listing an hour after
    /// somebody else has bought it.
    ///
    /// Raised on the fetch worker, not the framework thread, and after the book is stored, so a
    /// handler that reads the cache sees the new value rather than the one being replaced.
    /// </remarks>
    public event Action<string, uint>? BookChanged;

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

    /// <summary>Stops the fetcher and gives up on anything still queued.</summary>
    public void Dispose()
    {
        stopping.Cancel();
        queue.Clear();

        lock (waiting)
        {
            foreach (var request in requests)
                request.Completion.TrySetResult(new PricingResult(request.Total, request.Answered, request.FailedChunks));

            requests.Clear();
        }

        stopping.Dispose();
    }

    /// <summary>One caller's wait: the ids it asked for, and what has come back so far.</summary>
    private sealed class Request(
        string scope,
        FetchKind kind,
        HashSet<uint> outstanding,
        int total,
        Action<int, int>? onProgress)
    {
        public string Scope { get; } = scope;

        public FetchKind Kind { get; } = kind;

        public HashSet<uint> Outstanding { get; } = outstanding;

        public int Total { get; } = total;

        public Action<int, int>? OnProgress { get; } = onProgress;

        public int Answered { get; set; }

        public int FailedChunks { get; set; }

        public TaskCompletionSource<PricingResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly record struct BookSnapshot(OrderBook Book, DateTimeOffset Fetched);

    private readonly record struct SummarySnapshot(MarketSummary Summary, DateTimeOffset Fetched);
}

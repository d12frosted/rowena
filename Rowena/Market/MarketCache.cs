using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using Rowena.Core.Market;
using Rowena.Core.Universalis;

namespace Rowena.Market;

/// <summary>
/// Holds order books for a while so the window can be drawn every frame without hammering
/// a free service.
/// </summary>
/// <remarks>
/// Universalis is crowdsourced and costs nobody anything to use, which is exactly why it
/// deserves to be used politely: batched, cached, refreshed on a timer, and with a pause
/// between requests.
///
/// Batch size is not a matter of taste. Measured against Light, twenty ids in one request
/// answers reliably while fifty and a hundred both time out with a 504, twice each. So a large
/// sweep is many small requests rather than a few big ones, and it takes as long as it takes.
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
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(6),
    ];

    /// <summary>How far the gap between chunks is allowed to stretch once things start failing.</summary>
    private static readonly TimeSpan SlowestBetweenChunks = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<uint, Snapshot> books = new();

    private bool restored;

    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>True while any fetch is in flight, so nothing starts a second one.</summary>
    public bool Busy { get; private set; }

    public DateTimeOffset? LastRefresh { get; private set; }

    /// <summary>The last failure, kept so the window can say so instead of showing nothing.</summary>
    public string? LastError { get; private set; }

    public OrderBook? Book(uint itemId) => books.TryGetValue(itemId, out var snapshot) ? snapshot.Book : null;

    /// <summary>A lookup shaped for the evaluator.</summary>
    public Func<uint, OrderBook?> Lookup => Book;

    public bool IsStale(uint itemId) => IsStale(itemId, Ttl);

    /// <summary>
    /// Whether an item's price is older than the caller is willing to accept.
    /// </summary>
    /// <remarks>
    /// The age is the caller's business rather than one setting, because two questions here have
    /// completely different needs. Deciding whether to spend fifteen million on tokens wants
    /// depth from minutes ago. Deciding which of nine hundred furnishings is worth making wants a
    /// rough map, and one from this morning is fine, which is what makes a sweep affordable at
    /// twenty ids a request.
    /// </remarks>
    public bool IsStale(uint itemId, TimeSpan maxAge) =>
        !books.TryGetValue(itemId, out var snapshot) || DateTimeOffset.UtcNow - snapshot.Fetched > maxAge;

    /// <summary>Everything held, for writing to disk.</summary>
    public IEnumerable<(uint ItemId, OrderBook Book, DateTimeOffset Fetched)> Export() =>
        books.Select(entry => (entry.Key, entry.Value.Book, entry.Value.Fetched));

    /// <summary>What a previous session swept, if it is still worth having.</summary>
    public StoredSweep? RestoredSweep { get; private set; }

    /// <summary>
    /// Loads saved prices, once, as soon as the scope is known.
    /// </summary>
    /// <remarks>
    /// Not in the constructor, because which board we are pricing against is only knowable after a
    /// character is loaded, and prices from the wrong one are worse than none.
    /// </remarks>
    public void RestoreOnce(string scope, TimeSpan maxAge)
    {
        if (restored)
            return;

        restored = true;

        if (store.Load(scope, maxAge) is not { } loaded)
            return;

        foreach (var (itemId, book, fetched) in loaded.Books)
            books[itemId] = new Snapshot(book, fetched);

        RestoredSweep = loaded.Sweep;
    }

    public void Persist(string scope, StoredSweep? sweep)
    {
        if (!books.IsEmpty)
            store.Save(scope, Export(), sweep);
    }

    /// <summary>
    /// Fetches anything missing or past its shelf life. Returns immediately; the window
    /// keeps drawing whatever it already had.
    /// </summary>
    /// <param name="scope">
    /// Where to price against, resolved by the caller. It has to arrive already answered: this
    /// starts a background task, and working it out in there would mean reading game state off
    /// the framework thread, which throws.
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

        var wanted = force ? [.. itemIds] : itemIds.Where(IsStale).ToArray();
        if (wanted.Length == 0)
            return;

        _ = Task.Run(() => PriceAsync(scope, wanted, chunkSize: 20));
    }

    /// <param name="Answered">Ids that came back with something, empty book included.</param>
    /// <param name="FailedChunks">Batches given up on, each one a hole in the data.</param>
    public readonly record struct PricingResult(int Requested, int Answered, int FailedChunks)
    {
        /// <summary>Fraction of what was asked for that actually arrived.</summary>
        public double Coverage => Requested == 0 ? 1d : (double)Answered / Requested;
    }

    /// <summary>
    /// Prices a list of items in small sequential batches.
    /// </summary>
    /// <remarks>
    /// A chunk that fails every attempt is logged and skipped rather than failing the run. Losing
    /// twenty prices is a gap in one table; abandoning the run loses the other fourteen hundred too.
    ///
    /// But skipping quietly is its own trap: a run that lost most of its chunks looks exactly like
    /// a run that found nothing for sale, and that is a confident wrong answer. So the count comes
    /// back with the result and the caller is expected to care.
    ///
    /// The gap between chunks stretches as failures accumulate. A service returning 504s is asking
    /// to be left alone for a moment, and carrying on at the same rate is how a bad minute becomes
    /// a failed sweep.
    /// </remarks>
    public async Task<PricingResult> PriceAsync(
        string scope,
        IReadOnlyList<uint> itemIds,
        int chunkSize,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (Busy || itemIds.Count == 0)
            return new PricingResult(itemIds.Count, 0, 0);

        Busy = true;
        var covered = 0;
        var seen = 0;
        var failedChunks = 0;

        try
        {
            var chunks = itemIds.Chunk(Math.Max(1, chunkSize)).ToArray();

            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await Fetch(scope, chunk, cancellationToken).ConfigureAwait(false))
                    covered += chunk.Length;
                else
                    failedChunks++;

                seen += chunk.Length;
                onProgress?.Invoke(seen, itemIds.Count);

                if (seen < itemIds.Count)
                {
                    var pause = TimeSpan.FromMilliseconds(
                        Math.Min(SlowestBetweenChunks.TotalMilliseconds,
                                 BetweenChunks.TotalMilliseconds * (1 + failedChunks)));

                    await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
                }
            }

            LastRefresh = DateTimeOffset.UtcNow;

            if (covered > 0)
                LastError = null;
        }
        catch (OperationCanceledException)
        {
            // A cancelled sweep is not a failure worth reporting to the user.
        }
        catch (Exception error)
        {
            LastError = error.Message;
            log.Error(error, "Could not fetch prices.");
        }
        finally
        {
            Busy = false;
        }

        if (failedChunks > 0)
            log.Warning($"Priced {covered} of {itemIds.Count} ids; {failedChunks} batches were given up on.");

        return new PricingResult(itemIds.Count, covered, failedChunks);
    }

    private async Task<bool> Fetch(string scope, uint[] chunk, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                Store(await source.FetchAsync(scope, chunk, cancellationToken).ConfigureAwait(false), chunk);
                return true;
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
                log.Warning(error, $"Gave up on a batch of {chunk.Length}; those prices will be missing.");
            }
        }

        return false;
    }

    /// <summary>
    /// Records what came back, and an empty book for anything that did not.
    /// </summary>
    /// <remarks>
    /// Universalis omits items it has nothing for, which is mostly the untradable ones. Storing
    /// an empty book for those keeps "asked, and there is nothing listed" apart from "never
    /// asked", and stops every refresh from requesting them again forever.
    /// </remarks>
    private void Store(IReadOnlyDictionary<uint, OrderBook> fetched, IReadOnlyCollection<uint> requested)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var itemId in requested)
        {
            books[itemId] = new Snapshot(
                fetched.TryGetValue(itemId, out var book) ? book : OrderBook.Empty(itemId),
                now);
        }
    }

    private readonly record struct Snapshot(OrderBook Book, DateTimeOffset Fetched);
}

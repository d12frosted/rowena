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
internal sealed class MarketCache(IMarketDataSource source, IPluginLog log)
{
    /// <summary>One retry, because a 504 is usually the service and not the request.</summary>
    private const int Attempts = 2;

    private static readonly TimeSpan BetweenChunks = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan AfterFailure = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<uint, Snapshot> books = new();

    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>True while any fetch is in flight, so nothing starts a second one.</summary>
    public bool Busy { get; private set; }

    public DateTimeOffset? LastRefresh { get; private set; }

    /// <summary>The last failure, kept so the window can say so instead of showing nothing.</summary>
    public string? LastError { get; private set; }

    public OrderBook? Book(uint itemId) => books.TryGetValue(itemId, out var snapshot) ? snapshot.Book : null;

    /// <summary>A lookup shaped for the evaluator.</summary>
    public Func<uint, OrderBook?> Lookup => Book;

    public bool IsStale(uint itemId) =>
        !books.TryGetValue(itemId, out var snapshot) || DateTimeOffset.UtcNow - snapshot.Fetched > Ttl;

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

    /// <summary>
    /// Prices a list of items in small sequential batches.
    /// </summary>
    /// <returns>How many of the requested ids ended up with an answer.</returns>
    /// <remarks>
    /// A chunk that fails twice is logged and skipped rather than failing the sweep. Losing the
    /// prices for twenty items is a gap in one table; abandoning the run loses the other
    /// fourteen hundred as well.
    /// </remarks>
    public async Task<int> PriceAsync(
        string scope,
        IReadOnlyList<uint> itemIds,
        int chunkSize,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (Busy || itemIds.Count == 0)
            return 0;

        Busy = true;
        var covered = 0;
        var seen = 0;

        try
        {
            var chunks = itemIds.Chunk(Math.Max(1, chunkSize)).ToArray();

            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await Fetch(scope, chunk, cancellationToken).ConfigureAwait(false))
                    covered += chunk.Length;

                seen += chunk.Length;
                onProgress?.Invoke(seen, itemIds.Count);

                if (seen < itemIds.Count)
                    await Task.Delay(BetweenChunks, cancellationToken).ConfigureAwait(false);
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

        return covered;
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
                log.Verbose(error, $"Retrying a batch of {chunk.Length}.");
                await Task.Delay(AfterFailure, cancellationToken).ConfigureAwait(false);
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

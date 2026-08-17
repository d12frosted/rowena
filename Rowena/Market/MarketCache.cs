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
/// deserves to be used politely: one batched request, cached, refreshed on a timer rather
/// than on every draw.
/// </remarks>
internal sealed class MarketCache(IMarketDataSource source, IPluginLog log)
{
    private readonly ConcurrentDictionary<uint, Snapshot> books = new();

    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(10);

    public bool Refreshing { get; private set; }

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
        if (Refreshing)
            return;

        if (string.IsNullOrWhiteSpace(scope))
        {
            LastError = "Not logged in, and no data centre set.";
            return;
        }

        var wanted = force ? itemIds : [.. itemIds.Where(IsStale)];
        if (wanted.Count == 0)
            return;

        Refreshing = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var fetched = await source.FetchAsync(scope, wanted).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;

                foreach (var (itemId, book) in fetched)
                    books[itemId] = new Snapshot(book, now);

                LastRefresh = now;
                LastError = null;

                // Untradable items simply do not come back. Saying so once is more useful
                // than a window that silently shows nothing for them forever.
                var missing = wanted.Where(id => !fetched.ContainsKey(id)).ToArray();
                if (missing.Length > 0)
                    log.Information($"Universalis did not price {string.Join(", ", missing)}; likely untradable.");
            }
            catch (Exception error)
            {
                LastError = error.Message;
                log.Error(error, "Could not fetch prices.");
            }
            finally
            {
                Refreshing = false;
            }
        });
    }

    private readonly record struct Snapshot(OrderBook Book, DateTimeOffset Fetched);
}

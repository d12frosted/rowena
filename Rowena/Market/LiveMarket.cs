using Dalamud.Plugin.Services;
using Rowena.Core.Market;
using Rowena.Core.Universalis;
using Rowena.Game;

namespace Rowena.Market;

/// <summary>
/// Turns a push from Universalis into a refetch of the thing that moved.
/// </summary>
/// <remarks>
/// The feed says what changed; this decides whether anybody here cares and what to do about
/// it. What it does is never "believe the message": the feed sends deltas, and a book rebuilt
/// from deltas drifts. It queues an ordinary fetch of that one item instead, so the depth
/// everything is priced on stays something that was asked for and answered in full.
///
/// Something is worth refetching when a book is already held for it, which is exactly the set
/// of things some table is drawing. An item nobody has fetched is an item nobody is looking
/// at, and the feed carries thousands of those an hour.
///
/// A cooldown per item, because a busy item changes several times a minute and the price it
/// settles at is worth more than each step on the way. The queue already merges anything still
/// waiting, so this only guards against refetching in a loop once one lands.
/// </remarks>
internal sealed class LiveMarket : IDisposable
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(45);

    /// <summary>How often the boards being priced against are re-read.</summary>
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    private readonly MarketFeed feed;
    private readonly MarketCache market;
    private readonly IFramework framework;
    private readonly PricingScope scope;
    private readonly Worlds worlds;
    private readonly Configuration config;
    private readonly Diagnostics diagnostics;
    private readonly IPluginLog log;

    private readonly Dictionary<(string Scope, uint ItemId), DateTime> refetched = [];
    private readonly object gate = new();

    private string? buying;
    private string? selling;
    private uint[] buyingWorlds = [];
    private uint[] sellingWorlds = [];

    private DateTime nextAt;

    public LiveMarket(
        MarketFeed feed,
        MarketCache market,
        IFramework framework,
        PricingScope scope,
        Worlds worlds,
        Configuration config,
        Diagnostics diagnostics,
        IPluginLog log)
    {
        this.feed = feed;
        this.market = market;
        this.framework = framework;
        this.scope = scope;
        this.worlds = worlds;
        this.config = config;
        this.diagnostics = diagnostics;
        this.log = log;

        feed.Changed += OnChanged;
        framework.Update += Tick;
    }

    public void Dispose()
    {
        framework.Update -= Tick;
        feed.Changed -= OnChanged;
        feed.Dispose();
    }

    /// <summary>
    /// Keeps the feed pointed at the right boards, whether or not anything is on screen.
    /// </summary>
    /// <remarks>
    /// On the framework's clock rather than the window's, which was the bug this replaces: a
    /// cache that keeps itself current is precisely the thing that has to work while the window
    /// is shut, and following the board only while somebody watched it meant it never followed
    /// at all unless asked to. Reading which boards those are needs the framework thread anyway.
    /// </remarks>
    private void Tick(IFramework _)
    {
        if (DateTime.UtcNow < nextAt)
            return;

        nextAt = DateTime.UtcNow + Every;
        Follow(scope.Buying, scope.Selling);
    }

    /// <summary>Whether the feed is up.</summary>
    public bool Connected => config.LiveMarket && feed.Connected;

    /// <summary>How many changes have arrived, and how many were worth acting on.</summary>
    public long Received => feed.Received;

    public long Refetched { get; private set; }

    /// <summary>
    /// Points the feed at the boards being priced against.
    /// </summary>
    /// <remarks>
    /// Called as the window draws, which is often; the work is skipped unless the names have
    /// actually changed, and the feed itself ignores a set it is already watching.
    /// </remarks>
    private void Follow(string? nowBuying, string? nowSelling)
    {
        if (!config.LiveMarket)
        {
            feed.Watch([]);
            return;
        }

        if (nowBuying == buying && nowSelling == selling)
            return;

        buying = nowBuying;
        selling = nowSelling;
        buyingWorlds = [.. worlds.In(nowBuying)];
        sellingWorlds = [.. worlds.In(nowSelling)];

        diagnostics.Note(
            "live",
            $"following {nowBuying} ({buyingWorlds.Length} worlds) and {nowSelling} ({sellingWorlds.Length})");

        feed.Watch(buyingWorlds.Concat(sellingWorlds));
    }

    private void OnChanged(MarketChange change)
    {
        try
        {
            Refetch(buying, buyingWorlds, change);
            Refetch(selling, sellingWorlds, change);
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not act on a market change.");
        }
    }

    /// <summary>Queues a refetch when the change lands on a board we hold this item for.</summary>
    private void Refetch(string? scope, uint[] covered, MarketChange change)
    {
        if (string.IsNullOrWhiteSpace(scope) || !covered.Contains(change.WorldId))
            return;

        // Held means somebody is drawing it. Nothing else is worth a request.
        if (market.Book(scope, change.ItemId) is null)
            return;

        lock (gate)
        {
            var key = (scope, change.ItemId);

            if (refetched.TryGetValue(key, out var last) && DateTime.UtcNow - last < Cooldown)
            {
                diagnostics.Note("live", $"{change.Event} item {change.ItemId} on {scope}: still cooling down");
                return;
            }

            refetched[key] = DateTime.UtcNow;
        }

        Refetched++;
        diagnostics.Note("live", $"{change.Event} item {change.ItemId}: refetching on {scope}");
        market.RefreshInBackground(scope, [change.ItemId], force: true);
    }
}

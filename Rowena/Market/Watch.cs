using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using Rowena.Core.Market;
using Rowena.Game;
using Rowena.UI;

namespace Rowena.Market;

/// <summary>
/// Says something when a price moves, rather than when a timer goes off.
/// </summary>
/// <remarks>
/// The other alerts read the whole cache every few seconds and report whatever they find. That
/// is fine for a currency approaching its cap, which will still be true in a minute, and no use
/// at all for the two things worth knowing quickly: somebody undercutting a listing of mine,
/// and something appearing on the board for less than a vendor pays. The second is gone within
/// minutes and is worthless an hour later.
///
/// So the cache announces which book it replaced and only that item is looked at. The work is
/// proportional to what actually moved rather than to the size of the cache.
///
/// The announcement arrives on the fetch worker and the answers need the framework thread:
/// which world I am on and which cities my retainers stand in are both game memory. So the
/// event only writes an id into a queue, and a tick drains it. Still event driven, since
/// nothing is examined that did not change.
/// </remarks>
internal sealed class Watch : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(500);

    /// <summary>Enough of a queue for a sweep's worth of changes; past that, the oldest go.</summary>
    private const int Backlog = 512;

    private readonly IFramework framework;
    private readonly MarketCache market;
    private readonly BoardWatcher board;
    private readonly Boards boards;
    private readonly PricingScope scope;
    private readonly Items items;
    private readonly Configuration config;
    private readonly Notices notices;
    private readonly Diagnostics diagnostics;
    private readonly IPluginLog log;

    private readonly ConcurrentQueue<(string Scope, uint ItemId)> changed = [];
    private readonly HashSet<uint> undercutSaid = [];
    private readonly HashSet<uint> findSaid = [];

    private DateTime nextAt;
    private long looked;
    private string? buying;
    private string? selling;

    public Watch(
        IFramework framework,
        MarketCache market,
        BoardWatcher board,
        Boards boards,
        PricingScope scope,
        Items items,
        Configuration config,
        Notices notices,
        Diagnostics diagnostics,
        IPluginLog log)
    {
        this.framework = framework;
        this.market = market;
        this.board = board;
        this.boards = boards;
        this.scope = scope;
        this.items = items;
        this.config = config;
        this.notices = notices;
        this.diagnostics = diagnostics;
        this.log = log;

        market.BookChanged += OnChanged;
        framework.Update += Tick;
    }

    public void Dispose()
    {
        market.BookChanged -= OnChanged;
        framework.Update -= Tick;
    }

    /// <summary>How many changes are waiting, and how many have been looked at.</summary>
    public string Report =>
        config.AlertUndercut || config.AlertVendorFind
            ? $"{looked:N0} price moves looked at, {changed.Count} waiting"
            : "off";

    /// <summary>
    /// Notes that a book moved. Called on the fetch worker, so it does as little as possible.
    /// </summary>
    /// <remarks>
    /// Bounded, because a sweep replaces thousands of books in a few minutes and a queue that
    /// grew without limit would turn a wide sweep into a memory problem. Dropping the oldest is
    /// right: what is being looked for is a price that has just moved.
    /// </remarks>
    private void OnChanged(string forScope, uint itemId)
    {
        if (!config.AlertUndercut && !config.AlertVendorFind)
            return;

        changed.Enqueue((forScope, itemId));

        while (changed.Count > Backlog)
            changed.TryDequeue(out _);
    }

    private void Tick(IFramework _)
    {
        if (DateTime.UtcNow < nextAt)
            return;

        nextAt = DateTime.UtcNow + Every;

        // Read here, where reading them is allowed, for the handler to compare against.
        buying = scope.Buying;
        selling = scope.Selling;

        try
        {
            while (changed.TryDequeue(out var moved))
            {
                looked++;
                Look(moved.Scope, moved.ItemId);
            }
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not look at a price that moved.");
        }
    }

    private void Look(string forScope, uint itemId)
    {
        if (config.AlertUndercut && forScope == selling)
            Undercut(itemId);

        if (config.AlertVendorFind && forScope == buying)
            VendorFind(itemId);
    }

    /// <summary>
    /// Whether a listing of mine has fallen behind a queue worth caring about.
    /// </summary>
    /// <remarks>
    /// Not "somebody is cheaper than me", which is the easy question and usually the wrong one.
    /// The diagnosis the Selling tab uses is the same one here: three units ahead on a board
    /// selling ten a day are gone this afternoon and are not worth being told about, and two
    /// hundred ahead are a different matter.
    /// </remarks>
    private void Undercut(uint itemId)
    {
        if (board.Listed(itemId) is not { Count: > 0 } mine)
            return;

        var cheapest = mine.Min(listing => listing.UnitPrice);
        var units = mine.Where(listing => listing.UnitPrice == cheapest).Sum(listing => listing.Quantity);

        var reading = ListingDiagnosis.Of(
            cheapest,
            units,
            boards.Selling(itemId),
            boards.Vendor(itemId),
            board.TaxFor(mine[0].CityId),
            config.SellingHorizon());

        if (reading is not { } read)
            return;

        // Only the one this is for: a queue has grown in front of me that is longer than the
        // days of selling I said I wanted. The other verdicts are standing conditions rather
        // than things that just happened, and the Selling tab and the overview already carry
        // them: a price moving is not what makes a listing overpriced, and announcing that here
        // on the first fetch that touches it would be noise wearing an alert's clothes.
        if (read.Call is not ListingCall.Chase)
        {
            undercutSaid.Remove(itemId);
            return;
        }

        if (!undercutSaid.Add(itemId))
            return;

        notices.Add(
            NoticeKind.Undercut,
            $"{items.Name(itemId)}: {read.UnitsAhead:N0} units are now listed below your "
            + $"{read.Mine:N0}, {Phrases.Absorb(read.DaysToClear)} to clear. "
            + $"Matching {read.Floor:N0} costs {read.Haircut:N0} a unit.");
    }

    /// <summary>
    /// Whether something just appeared for less than a vendor pays.
    /// </summary>
    /// <remarks>
    /// The one alert that is worthless late. These are gone within minutes because they are
    /// underpriced by definition and anybody else watching can see them too.
    /// </remarks>
    private void VendorFind(uint itemId)
    {
        var vendorPrice = boards.Vendor(itemId);

        if (vendorPrice <= 0 || boards.Buying(itemId) is not { } book)
            return;

        var found = VendorArbitrage.Find(book, vendorPrice, boards.Tax);

        if (found.Profit < config.VendorFindFloor)
        {
            findSaid.Remove(itemId);
            return;
        }

        if (!findSaid.Add(itemId))
            return;

        var where = found.Best is { } best ? $" on {best.World}" : "";

        notices.Add(
            NoticeKind.VendorFind,
            $"{items.Name(itemId)}: {found.Units} units{where} are listed under what a vendor pays, "
            + $"{found.Profit:N0} gil.");

        diagnostics.Note("watch", $"vendor find on {itemId}: {found.Profit:N0}");
    }
}

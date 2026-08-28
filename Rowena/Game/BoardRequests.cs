using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Rowena.Core.Market;
using Rowena.Market;

namespace Rowena.Game;

/// <summary>
/// Asks the game server what a board holds, item by item, and banks the answers.
/// </summary>
/// <remarks>
/// <see cref="BoardWatcher"/> reads whatever board views happen past and can never say which
/// world they describe. This side of the problem is different: a request this plugin sends
/// itself is answered about the world the character is standing on, so the one fact a view
/// is missing is known before it is asked for. That is what makes the packets filable, and
/// why this only serves a scope that is the current world; anything else stays Universalis's.
///
/// The asking is the same call a price-check plugin makes: the item search proxy, given an
/// item and told to request, no window involved. The server answers in pages that arrive as
/// ordinary board packets, so the reading is assembled by <see cref="BoardReading"/> and
/// stored through the one cache everything already draws from.
///
/// One item at a time, a breath apart, because the server throttles board requests and a
/// refused request is silence rather than an error. Slow is the price of exact: twenty
/// listings is most of half a minute, against one Universalis call, and the caller chose
/// this knowing that. An item the board goes quiet on falls back to Universalis rather than
/// landing nowhere, so a press always fills every row it named one way or the other.
/// </remarks>
internal sealed class BoardRequests : IDisposable
{
    /// <summary>The breath between one item answered and the next asked.</summary>
    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(1);

    /// <summary>How long one item gets before it is given up on.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The longer breath after a request the server ignored.
    /// </summary>
    /// <remarks>
    /// Measured on Shiva: at a request every couple of seconds, roughly every sixth is
    /// dropped without a word, which reads as a sliding rate window rather than bad luck.
    /// Silence is the server asking for room, so the next request gives it some.
    /// </remarks>
    private static readonly TimeSpan AfterSilence = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a quiet line means the answer is over.
    /// </summary>
    /// <remarks>
    /// Two waits share it. A board holding an exact multiple of a page never sends the short
    /// page that says "done", so a full page followed by silence has to be read as the end.
    /// And the sales history is a separate packet on no fixed schedule, so a reading whose
    /// pages have ended gives it this long to arrive before the book is banked without it.
    /// </remarks>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(600);

    private readonly IMarketBoard board;
    private readonly IFramework framework;
    private readonly Balances balances;
    private readonly MarketCache market;
    private readonly Configuration config;
    private readonly Diagnostics diagnostics;
    private readonly IPluginLog log;

    private readonly Queue<uint> queue = new();
    private readonly HashSet<uint> queued = [];
    private readonly HashSet<uint> retried = [];
    private readonly object gate = new();

    private Pending? current;
    private string? scope;
    private DateTime nextSendAt;

    // The run being measured: a refresh is a press somebody is waiting on, and "it is slow"
    // is only actionable when the slowness can be pointed at. Each item accounts for its
    // answer; the run's line says what remains, which is the deliberate spacing.
    private bool running;
    private DateTime runStartedAt;
    private int runBanked;
    private int runHandedOver;

    public BoardRequests(
        IMarketBoard board,
        IFramework framework,
        Balances balances,
        MarketCache market,
        Configuration config,
        Diagnostics diagnostics,
        IPluginLog log)
    {
        this.board = board;
        this.framework = framework;
        this.balances = balances;
        this.market = market;
        this.config = config;
        this.diagnostics = diagnostics;
        this.log = log;

        board.OfferingsReceived += OnOfferings;
        board.HistoryReceived += OnHistory;
        framework.Update += Tick;
    }

    public void Dispose()
    {
        framework.Update -= Tick;
        board.HistoryReceived -= OnHistory;
        board.OfferingsReceived -= OnOfferings;
    }

    /// <summary>Whether anything is still being asked about.</summary>
    public bool Busy
    {
        get
        {
            lock (gate)
                return current is not null || queue.Count > 0;
        }
    }

    /// <summary>
    /// Whether the board itself can answer for this scope: only when it is the world under
    /// the character's feet, since that is the world the packets will describe.
    /// </summary>
    /// <remarks>Reads the logged-in world, so it is the framework thread's to ask.</remarks>
    public bool CanServe(string? where) =>
        config.BoardRefresh
        && !string.IsNullOrWhiteSpace(where)
        && string.Equals(balances.HomeWorld, where, StringComparison.Ordinal);

    /// <summary>
    /// Starts asking the board about these items, oldest press first.
    /// </summary>
    /// <returns>
    /// False when the board cannot answer from here, in which case nothing was queued and
    /// the caller should ask Universalis the way it always did.
    /// </returns>
    public bool Refresh(string? where, IReadOnlyCollection<uint> itemIds)
    {
        if (!CanServe(where))
            return false;

        lock (gate)
        {
            scope = where;

            if (!running && current is null && queue.Count == 0)
            {
                running = true;
                runStartedAt = DateTime.UtcNow;
                runBanked = 0;
                runHandedOver = 0;
                retried.Clear();
            }

            foreach (var itemId in itemIds)
            {
                if (queued.Add(itemId))
                    queue.Enqueue(itemId);
            }
        }

        diagnostics.Note("board", $"asking the board about {itemIds.Count} items on {where}");
        return true;
    }

    /// <summary>
    /// The clock: finishes the item in hand, then asks about the next one.
    /// </summary>
    /// <remarks>
    /// Decisions are taken under the lock and acted on outside it, because banking a book
    /// raises <see cref="MarketCache.BookChanged"/> and a handler is nobody to hold a lock
    /// over.
    /// </remarks>
    private void Tick(IFramework _)
    {
        Pending? bank = null;
        uint fallback = 0;
        var silentFor = TimeSpan.Zero;
        var send = false;
        var summary = default(string?);
        var retry = default(string?);

        lock (gate)
        {
            var now = DateTime.UtcNow;

            if (current is { } pending)
            {
                var over = pending.Reading.Ended
                    ? pending.Reading.SalesSeen || now - pending.LastHeard > Quiet
                    : pending.Heard && now - pending.LastHeard > Quiet;

                if (!over && now <= pending.Deadline)
                    return;

                current = null;

                if (pending.Heard)
                {
                    bank = pending;
                    nextSendAt = now + Spacing;
                }
                else if (retried.Add(pending.ItemId))
                {
                    // Once more, from the back of the queue: the silence is the server's
                    // rate window, and by the time the turn comes round again it has moved.
                    queued.Add(pending.ItemId);
                    queue.Enqueue(pending.ItemId);
                    retry = $"item {pending.ItemId}: silent for {(int)(now - pending.SentAt).TotalMilliseconds}ms, "
                        + "will ask again after the rest";
                    nextSendAt = now + AfterSilence;
                }
                else
                {
                    fallback = pending.ItemId;
                    silentFor = now - pending.SentAt;
                    nextSendAt = now + AfterSilence;
                }
            }
            else if (queue.Count > 0)
            {
                if (now >= nextSendAt)
                    send = true;
            }
            else if (running)
            {
                // Everything asked has been answered or handed over: the run is the press
                // somebody timed, so it accounts for itself in one line.
                running = false;
                summary = $"run over: {runBanked} read off the board, {runHandedOver} handed to Universalis, "
                    + $"{(now - runStartedAt).TotalSeconds:F1}s all told";
            }
        }

        if (bank is not null)
            Bank(bank);

        if (retry is not null)
            diagnostics.Note("board", retry);

        if (fallback != 0)
            GiveUpOn(fallback, $"silent again, {(int)silentFor.TotalMilliseconds}ms this time");

        if (send)
            SendNext();

        if (summary is not null)
            diagnostics.Note("board", summary);
    }

    /// <summary>Banks a finished reading as an ordinary book, exact and stamped now.</summary>
    private void Bank(Pending pending)
    {
        if (scope is not { } where)
            return;

        var book = pending.Reading.Book(DateTimeOffset.UtcNow);
        market.Absorb(where, book);

        lock (gate)
            runBanked++;

        // The timings are the point: "slow" is only fixable once it says which wait it is.
        var now = DateTime.UtcNow;
        var firstWord = pending.FirstHeardAt is { } heard
            ? $"first word in {(int)(heard - pending.SentAt).TotalMilliseconds}ms, "
            : "";

        diagnostics.Note(
            "board",
            $"item {pending.ItemId}: {pending.Pages} pages, {book.Listings.Count} listings, "
            + $"{book.RecentSales.Count} sales; {firstWord}"
            + $"banked in {(int)(now - pending.SentAt).TotalMilliseconds}ms"
            + (pending.Reading.SalesSeen ? "" : ", history never came"));
    }

    /// <summary>An item the board would not answer goes to Universalis, so the row still fills.</summary>
    private void GiveUpOn(uint itemId, string why)
    {
        diagnostics.Note("board", $"item {itemId}: {why}, asking Universalis");

        lock (gate)
            runHandedOver++;

        if (scope is { } where)
            market.RefreshInBackground(where, [itemId], force: true, FetchPriority.Interactive);
    }

    /// <summary>
    /// Asks about the next item, unless the scope has stopped being the world we stand on,
    /// in which case everything still queued goes to Universalis instead.
    /// </summary>
    private void SendNext()
    {
        if (!CanServe(scope))
        {
            HandOver("no longer standing on the board being asked about");
            return;
        }

        uint itemId;
        var now = DateTime.UtcNow;

        lock (gate)
        {
            if (queue.Count == 0)
                return;

            itemId = queue.Dequeue();
            queued.Remove(itemId);
        }

        if (Send(itemId, out var requestId))
        {
            lock (gate)
                current = new Pending(itemId, new BoardReading(itemId, scope!), requestId, now + Patience);
        }
        else
        {
            lock (gate)
                nextSendAt = now + Spacing;

            GiveUpOn(itemId, "the request would not send");
        }
    }

    /// <summary>Gives everything still queued to Universalis, and says why in one line.</summary>
    private void HandOver(string why)
    {
        uint[] rest;

        lock (gate)
        {
            rest = [.. queue];
            queue.Clear();
            queued.Clear();
            runHandedOver += rest.Length;
        }

        if (rest.Length == 0)
            return;

        diagnostics.Note("board", $"{why}: {rest.Length} items go to Universalis");

        if (scope is { } where)
            market.RefreshInBackground(where, rest, force: true, FetchPriority.Interactive);
    }

    /// <summary>
    /// Hands the proxy one item to ask the server about.
    /// </summary>
    /// <remarks>
    /// Deliberately without consulting <c>WaitingForListings</c> first. Measured at a bell,
    /// that flag reads true from the moment the sell list opens and never clears, because the
    /// retainer's own listings are served through this same proxy; meanwhile requests sent
    /// regardless, the player's compare-prices click among them, are answered normally. So
    /// the flag cannot mean "a request is in flight" here, and gating on it meant never
    /// sending at all in the one place this class exists for. A request that really cannot
    /// go says so by returning false, or by silence, and both already fall back.
    /// </remarks>
    private unsafe bool Send(uint itemId, out int requestId)
    {
        requestId = -1;

        try
        {
            var proxy = InfoProxyItemSearch.Instance();

            if (proxy is null)
                return false;

            proxy->SearchItemId = itemId;

            if (!proxy->RequestData())
                return false;

            requestId = proxy->CurrentRequestId;
            return true;
        }
        catch (Exception error)
        {
            log.Warning(error, $"Could not ask the board about item {itemId}.");
            return false;
        }
    }

    /// <summary>
    /// Reads one page of our answer in.
    /// </summary>
    /// <remarks>
    /// A page with listings is ours when they are all about our item; the request id merely
    /// corroborates. An empty page carries no item to match, so there the id is the only
    /// word, and a wrong one is left alone: worst case the deadline passes and Universalis
    /// answers instead, which errs the survivable way round.
    /// </remarks>
    private void OnOfferings(IMarketBoardCurrentOfferings offerings)
    {
        lock (gate)
        {
            if (current is not { } pending)
                return;

            var ours = offerings.ItemListings.Count > 0
                ? offerings.ItemListings.All(listing => listing.ItemId == pending.ItemId)
                : offerings.RequestId == pending.RequestId;

            if (!ours)
                return;

            pending.Reading.Add(
            [
                .. offerings.ItemListings.Select(listing => new BoardOffer(
                    (long)listing.PricePerUnit,
                    (int)listing.ItemQuantity,
                    listing.IsHq)),
            ]);

            pending.Heard = true;
            pending.Pages++;
            pending.FirstHeardAt ??= DateTime.UtcNow;
            pending.LastHeard = DateTime.UtcNow;
        }
    }

    /// <summary>Reads the sales half of our answer in: per unit, newest first, as sent.</summary>
    private void OnHistory(IMarketBoardHistory history)
    {
        lock (gate)
        {
            if (current is not { } pending || history.ItemId != pending.ItemId)
                return;

            pending.Reading.Sales([.. history.HistoryListings.Select(sale => (long)sale.SalePrice)]);
            pending.LastHeard = DateTime.UtcNow;
        }
    }

    /// <summary>The item being waited on: its reading so far, and how long it has left.</summary>
    private sealed class Pending(uint itemId, BoardReading reading, int requestId, DateTime deadline)
    {
        public uint ItemId { get; } = itemId;

        public BoardReading Reading { get; } = reading;

        public int RequestId { get; } = requestId;

        public DateTime Deadline { get; } = deadline;

        /// <summary>Whether any page has arrived at all.</summary>
        public bool Heard { get; set; }

        /// <summary>How many pages have, for saying where a slow answer spent its time.</summary>
        public int Pages { get; set; }

        public DateTime SentAt { get; } = DateTime.UtcNow;

        public DateTime? FirstHeardAt { get; set; }

        public DateTime LastHeard { get; set; } = DateTime.UtcNow;
    }
}

using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Rowena.Core.Market;

namespace Rowena.Game;

/// <summary>
/// What the game itself says about the board, which is exact and costs nothing.
/// </summary>
/// <remarks>
/// Universalis is whatever somebody's client last uploaded. The client's own packets are what
/// was actually there, at the moment it was there, and they arrive free whenever the board is
/// opened. Two things here are worth having and both are unambiguous.
///
/// The tax rates, because the seller's cut is nought to five percent by city and moves daily,
/// and everything else in this plugin has been assuming the worst. Knowing them turns "park
/// your retainers somewhere cheap" from folklore into a number.
///
/// My own listings, because a listing carrying one of my retainer ids is mine wherever the
/// board view came from. Not the whole of them: a board view is about one item, so it only
/// ever says what I have out of the thing I searched for. The whole list is the retainers'
/// own market slots, read whenever one is opened, and what the board adds is a fresher word
/// on one item than the slots have, since a listing can sell between two visits. The two are
/// folded together by <see cref="KnownListings"/> and that is what <see cref="Listed"/> serves.
///
/// What is deliberately not taken from here is the board as an order book. A listing carries
/// no world, so a view that merely happened past cannot be attributed to a world or a data
/// centre, and filing cross-world listings under the wrong board would quietly corrupt the
/// one thing this plugin is careful about. A view this plugin asked for itself is different,
/// because the asker knows where it was standing: that is <see cref="BoardRequests"/>, and
/// for everything else Universalis stays the source for depth.
/// </remarks>
internal sealed class BoardWatcher : IDisposable
{
    private readonly IMarketBoard board;
    private readonly Configuration config;
    private readonly Action save;
    private readonly Diagnostics diagnostics;
    private readonly IPluginLog log;

    private readonly Dictionary<uint, (int Request, DateTimeOffset SeenAt, List<RetainerListing> Listings)> mine = [];
    private readonly object gate = new();

    private IReadOnlyList<RetainerListing> known = [];
    private DateTime knownAt;

    private IReadOnlyList<uint> towns = [];
    private DateTime townsReadAt;

    public BoardWatcher(
        IMarketBoard board,
        Configuration config,
        Action save,
        Diagnostics diagnostics,
        IPluginLog log)
    {
        this.board = board;
        this.config = config;
        this.save = save;
        this.diagnostics = diagnostics;
        this.log = log;

        // What the game said last time, if it is still true. The rates hold for hours and are
        // only offered when asked for, so throwing them away on every reload means assuming
        // the worst for no reason.
        // A listing written down before the retainer id was kept cannot be matched against its
        // retainer's slots, so it would sit beside them as a duplicate. The slots cover it.
        foreach (var group in config.MyListings.Where(listing => listing.RetainerId != 0).GroupBy(listing => listing.ItemId))
        {
            mine[group.Key] = (
                0,
                DateTimeOffset.FromUnixTimeSeconds(group.Max(listing => listing.SeenAt)),
                [
                    .. group.Select(listing => new RetainerListing(
                        listing.RetainerId,
                        listing.Retainer,
                        listing.CityId,
                        listing.ItemId,
                        listing.UnitPrice,
                        listing.Quantity,
                        listing.IsHq)),
                ]);
        }

        if (config.SellerRates.Count > 0
            && DateTimeOffset.FromUnixTimeSeconds(config.SellerRatesUntil) > DateTimeOffset.UtcNow)
        {
            reported = config.SellerRates;
            RatesValidUntil = DateTimeOffset.FromUnixTimeSeconds(config.SellerRatesUntil);
        }

        board.OfferingsReceived += OnOfferings;
        board.TaxRatesReceived += OnTaxRates;
        board.HistoryReceived += OnHistory;
        board.ItemPurchased += OnPurchased;

        diagnostics.Note("board", "listening for market board packets");
    }

    public void Dispose()
    {
        board.OfferingsReceived -= OnOfferings;
        board.TaxRatesReceived -= OnTaxRates;
        board.HistoryReceived -= OnHistory;
        board.ItemPurchased -= OnPurchased;
    }

    /// <summary>
    /// Noted only, for now.
    /// </summary>
    /// <remarks>
    /// History carries what actually sold and for how much, which is the measured half of
    /// every forecast here. It is not used yet because it has the same problem the offerings
    /// have: a view carries no world, so it cannot be filed against a board. Listening now
    /// says whether the packets arrive at all, which is the question in front of it.
    /// </remarks>
    private void OnHistory(IMarketBoardHistory history) =>
        diagnostics.Note("board", $"history for item {history.ItemId}: {history.HistoryListings.Count} sales");

    private void OnPurchased(IMarketBoardPurchase purchase) =>
        diagnostics.Note("board", $"bought item {purchase.CatalogId} x{purchase.ItemQuantity}");

    /// <summary>
    /// The seller's cut in each city, once the game has said and while it still holds.
    /// </summary>
    /// <remarks>
    /// A reduced rate is a promotion with an end on it, and the game says when: past that,
    /// these are not rates, they are what the rates used to be. Null then, so everything falls
    /// back to assuming the worst rather than quietly quoting yesterday's discount.
    /// </remarks>
    public IReadOnlyDictionary<uint, double>? SellerRates =>
        RatesValidUntil > DateTimeOffset.UtcNow ? reported : null;

    private IReadOnlyDictionary<uint, double>? reported;

    /// <summary>When the reported rates stop being trustworthy.</summary>
    public DateTimeOffset? RatesValidUntil { get; private set; }

    /// <summary>What I have listed for an item, from whichever source has looked most recently.</summary>
    public IReadOnlyList<RetainerListing> Listed(uint itemId) =>
        [.. Known().Where(listing => listing.ItemId == itemId)];

    /// <summary>Every item I have something listed for.</summary>
    public IReadOnlyCollection<uint> ListedItems() =>
        [.. Known().Select(listing => listing.ItemId).Distinct()];

    /// <summary>Every listing I have out, on every retainer that has been looked at.</summary>
    public IReadOnlyList<RetainerListing> Known()
    {
        // Asked for once a row while drawing, and the answer only moves when a retainer or a
        // board is opened, so a second-old answer is the same answer.
        if (DateTime.UtcNow - knownAt < TimeSpan.FromSeconds(1))
            return known;

        List<BoardSighting> sightings;

        lock (gate)
        {
            sightings =
            [
                .. mine.Select(entry => new BoardSighting(entry.Key, entry.Value.SeenAt, [.. entry.Value.Listings])),
            ];
        }

        var retainers = config.Retainers
            .Select(retainer => new SeenRetainer(
                retainer.RetainerId,
                string.IsNullOrWhiteSpace(retainer.Name) ? "a retainer" : retainer.Name,
                retainer.CityId,
                DateTimeOffset.FromUnixTimeSeconds(retainer.SeenAt),
                [.. retainer.Slots.Select(slot => new MarketSlot(slot.ItemId, slot.Quantity, slot.UnitPrice, slot.IsHq))]))
            .ToList();

        known = KnownListings.Merge(retainers, sightings);
        knownAt = DateTime.UtcNow;
        return known;
    }

    /// <summary>How many retainers I have, and how many of them have been opened and read.</summary>
    public (int Seen, int Of) RetainersSeen() => (config.Retainers.Count, RetainerCount());

    private int retainerCount;

    private int RetainerCount()
    {
        RetainerTowns();
        return retainerCount;
    }

    /// <summary>
    /// The tax to price with: the buyer's flat cut, and the worst of the cities I actually
    /// sell from.
    /// </summary>
    /// <remarks>
    /// The worst rather than the best, because which retainer a thing sells from is not known
    /// when it is priced, and a rate that flatters the answer is the failure mode this plugin
    /// exists to avoid. Cities I do not keep a retainer in do not count: their rate is not an
    /// option I have. With nothing known yet, the maximum stands, as it always did.
    ///
    /// The cities come from the retainers themselves rather than from listings I have happened
    /// to see on the board, so this is right from the moment the game says what the rates are
    /// rather than waiting for me to look up something I am selling.
    /// </remarks>
    /// <summary>
    /// The cut for one city, when the game has said what it is.
    /// </summary>
    /// <remarks>
    /// For pricing something that has already sold from a known retainer, where the city is a
    /// fact rather than a guess. <see cref="Tax"/> assumes the worst of the cities you have
    /// retainers in, because it is pricing something not yet sold.
    /// </remarks>
    public MarketTax TaxFor(uint cityId) =>
        SellerRates is { } rates && rates.TryGetValue(cityId, out var rate)
            ? Tax().WithSellerRate(rate)
            : Tax();

    public MarketTax Tax()
    {
        if (SellerRates is not { Count: > 0 } rates)
            return MarketTax.Standard;

        var worst = RetainerTowns()
            .Select(town => rates.TryGetValue(town, out var rate) ? rate : (double?)null)
            .Where(rate => rate is not null)
            .Select(rate => rate!.Value)
            .DefaultIfEmpty(MarketTax.Standard.SellerRate)
            .Max();

        return MarketTax.Standard.WithSellerRate(worst);
    }

    /// <summary>
    /// The cities my retainers stand in, which is what decides the cut on anything they sell.
    /// </summary>
    /// <remarks>
    /// Game memory, so it is read on the thread that draws and cached for a while: retainers
    /// do not move without a trip to a bell, and this is asked several times a second.
    /// </remarks>
    public IReadOnlyList<uint> RetainerTowns()
    {
        if (DateTime.UtcNow - townsReadAt < TimeSpan.FromSeconds(30))
            return towns;

        townsReadAt = DateTime.UtcNow;
        towns = ReadRetainerTowns();
        return towns;
    }

    private unsafe IReadOnlyList<uint> ReadRetainerTowns()
    {
        var manager = RetainerManager.Instance();

        if (manager is null || !manager->IsReady)
            return towns;

        var found = new List<uint>();

        for (var index = 0u; index < manager->GetRetainerCount(); index++)
        {
            var retainer = manager->GetRetainerBySortedIndex(index);

            if (retainer is not null && retainer->RetainerId != 0)
                found.Add((uint)retainer->Town);
        }

        retainerCount = found.Count;
        return [.. found.Distinct()];
    }

    private void OnTaxRates(IMarketTaxRates rates)
    {
        diagnostics.Note("board", "tax rates packet arrived");

        reported = new Dictionary<uint, double>
        {
            [1] = rates.LimsaLominsaTax / 100d,
            [2] = rates.GridaniaTax / 100d,
            [3] = rates.UldahTax / 100d,
            [4] = rates.IshgardTax / 100d,
            [7] = rates.KuganeTax / 100d,
            [10] = rates.CrystariumTax / 100d,
            [12] = rates.SharlayanTax / 100d,
            [14] = rates.TuliyollalTax / 100d,
        };

        RatesValidUntil = new DateTimeOffset(rates.ValidUntil.ToUniversalTime(), TimeSpan.Zero);
        log.Verbose($"Market tax rates received, valid until {RatesValidUntil}.");

        config.SellerRates = new Dictionary<uint, double>(reported!);
        config.SellerRatesUntil = RatesValidUntil.Value.ToUnixTimeSeconds();
        save();

        diagnostics.Note(
            "board",
            $"tax rates received, seller pays {reported!.Values.Min():P0} to {reported.Values.Max():P0}, "
            + $"until {RatesValidUntil:HH:mm}");
    }

    /// <summary>
    /// Picks my own retainers' listings out of whatever board view just arrived.
    /// </summary>
    /// <remarks>
    /// The board answers in pages, and each page is a page rather than the whole answer: asking
    /// about one item produced ten listings holding four of mine and then two more holding none,
    /// and treating the second as the truth threw the first away. Pages of one request are
    /// gathered together, and a new request for the same item starts again, so a listing that
    /// has sold does stop being reported.
    /// </remarks>
    private void OnOfferings(IMarketBoardCurrentOfferings offerings)
    {
        diagnostics.Note("board", $"offerings packet arrived: {offerings.ItemListings.Count} listings");

        try
        {
            var retainers = MyRetainers();

            if (retainers.Count == 0)
            {
                diagnostics.Note(
                    "board",
                    $"offerings for {offerings.ItemListings.Count} listings, but no retainers are known yet");
                return;
            }

            foreach (var group in offerings.ItemListings.GroupBy(listing => listing.ItemId))
            {
                var listed = group
                    .Where(listing => retainers.Contains(listing.RetainerId))
                    .Select(listing => new RetainerListing(
                        listing.RetainerId,
                        listing.RetainerName,
                        (uint)listing.RetainerCityId,
                        listing.ItemId,
                        (long)listing.PricePerUnit,
                        (int)listing.ItemQuantity,
                        listing.IsHq))
                    .ToList();

                int total;

                lock (gate)
                {
                    if (mine.TryGetValue(group.Key, out var seen) && seen.Request == offerings.RequestId)
                        seen.Listings.AddRange(listed);
                    else
                        mine[group.Key] = (offerings.RequestId, DateTimeOffset.UtcNow, listed);

                    total = mine[group.Key].Listings.Count;
                }

                if (listed.Count > 0)
                {
                    diagnostics.Note(
                        "board",
                        $"item {group.Key}: {listed.Count} of this page is mine, {total} so far");
                }

                Remember();
            }
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not read the board offerings.");
        }
    }

    /// <summary>
    /// Writes what is listed out, so a reload does not mean going and looking again.
    /// </summary>
    private void Remember()
    {
        lock (gate)
        {
            config.MyListings =
            [
                .. mine.SelectMany(entry => entry.Value.Listings.Select(listing => new StoredListing
                {
                    ItemId = listing.ItemId,
                    UnitPrice = listing.UnitPrice,
                    Quantity = listing.Quantity,
                    IsHq = listing.IsHq,
                    Retainer = listing.Retainer,
                    RetainerId = listing.RetainerId,
                    CityId = listing.CityId,
                    SeenAt = entry.Value.SeenAt.ToUnixTimeSeconds(),
                })),
            ];
        }

        save();
    }

    /// <summary>My retainers' ids, so a listing can be recognised as mine.</summary>
    private unsafe HashSet<ulong> MyRetainers()
    {
        var manager = RetainerManager.Instance();

        if (manager is null || !manager->IsReady)
            return [];

        var ids = new HashSet<ulong>();

        for (var index = 0u; index < manager->GetRetainerCount(); index++)
        {
            var retainer = manager->GetRetainerBySortedIndex(index);

            if (retainer is not null && retainer->RetainerId != 0)
                ids.Add(retainer->RetainerId);
        }

        return ids;
    }
}

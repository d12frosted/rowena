using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Rowena.Core.Market;

namespace Rowena.Game;

/// <summary>One of my retainer's listings, as the board itself reported it.</summary>
/// <param name="CityId">Where the retainer stands, which is what decides the seller's tax.</param>
internal readonly record struct MyListing(
    uint ItemId,
    long UnitPrice,
    int Quantity,
    bool IsHq,
    string Retainer,
    uint CityId);

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
/// board view came from. That is the foundation the selling side needs: what I have out, at
/// what price, in which city.
///
/// What is deliberately not taken from here is the board as an order book. A listing carries
/// no world, so a view cannot be attributed to a world or a data centre, and filing
/// cross-world listings under the wrong board would quietly corrupt the one thing this plugin
/// is careful about. Until that can be established, Universalis stays the source for depth.
/// </remarks>
internal sealed class BoardWatcher : IDisposable
{
    private readonly IMarketBoard board;
    private readonly Configuration config;
    private readonly Action save;
    private readonly Diagnostics diagnostics;
    private readonly IPluginLog log;

    private readonly Dictionary<uint, List<MyListing>> mine = [];
    private readonly object gate = new();

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

    /// <summary>What I have listed for an item, newest sighting wins.</summary>
    public IReadOnlyList<MyListing> Listed(uint itemId)
    {
        lock (gate)
            return mine.TryGetValue(itemId, out var listings) ? [.. listings] : [];
    }

    /// <summary>Every item I have something listed for.</summary>
    public IReadOnlyCollection<uint> ListedItems()
    {
        lock (gate)
            return [.. mine.Keys];
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
    /// Only the items the view was about are touched. A view holding none of mine means I have
    /// none listed for that item, which is news worth recording rather than an empty result to
    /// ignore: it is how a sold-out listing stops being reported as still standing.
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
                    .Select(listing => new MyListing(
                        listing.ItemId,
                        (long)listing.PricePerUnit,
                        (int)listing.ItemQuantity,
                        listing.IsHq,
                        listing.RetainerName,
                        (uint)listing.RetainerCityId))
                    .ToList();

                lock (gate)
                {
                    if (listed.Count > 0)
                        mine[group.Key] = listed;
                    else
                        mine.Remove(group.Key);
                }

                if (listed.Count > 0)
                    diagnostics.Note("board", $"item {group.Key}: {listed.Count} of the listings are mine");
            }
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not read the board offerings.");
        }
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

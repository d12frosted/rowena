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
    private readonly Diagnostics diagnostics;
    private readonly IPluginLog log;

    private readonly Dictionary<uint, List<MyListing>> mine = [];
    private readonly object gate = new();

    public BoardWatcher(IMarketBoard board, Diagnostics diagnostics, IPluginLog log)
    {
        this.board = board;
        this.diagnostics = diagnostics;
        this.log = log;

        board.OfferingsReceived += OnOfferings;
        board.TaxRatesReceived += OnTaxRates;
    }

    public void Dispose()
    {
        board.OfferingsReceived -= OnOfferings;
        board.TaxRatesReceived -= OnTaxRates;
    }

    /// <summary>The seller's cut in each city, once the game has said. Null until it has.</summary>
    public IReadOnlyDictionary<uint, double>? SellerRates { get; private set; }

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
    /// exists to avoid. Cities I have never listed from do not count: their rate is not an
    /// option I am taking. With nothing seen yet, the maximum stands, as it always did.
    /// </remarks>
    public MarketTax Tax()
    {
        if (SellerRates is not { Count: > 0 } rates)
            return MarketTax.Standard;

        List<uint> cities;

        lock (gate)
            cities = [.. mine.Values.SelectMany(listings => listings).Select(listing => listing.CityId).Distinct()];

        var worst = cities
            .Select(city => rates.TryGetValue(city, out var rate) ? rate : (double?)null)
            .Where(rate => rate is not null)
            .Select(rate => rate!.Value)
            .DefaultIfEmpty(MarketTax.Standard.SellerRate)
            .Max();

        return MarketTax.Standard.WithSellerRate(worst);
    }

    private void OnTaxRates(IMarketTaxRates rates)
    {
        SellerRates = new Dictionary<uint, double>
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

        diagnostics.Note(
            "board",
            $"tax rates received, seller pays {SellerRates!.Values.Min():P0} to {SellerRates.Values.Max():P0}");
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

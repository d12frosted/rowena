using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Rowena.Core.Conversions;
using Rowena.Core.Market;
using Rowena.Game;
using Rowena.IPC;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// One window: what you are holding, and what it is worth turning into.
/// </summary>
/// <remarks>
/// Deliberately not a market browser, and deliberately not a control panel either. The only
/// questions it answers are the ones that need both halves of the picture, your balances and
/// the depth of the board, because either one alone is already covered by something else.
///
/// A shell, and nothing else: the strip that never scrolls away, a tab per question, and the
/// business of picking up where the last session left off. The questions themselves are asked one
/// tab at a time, which is also what makes the hidden ones free.
///
/// Tabbed rather than stacked because the questions run on different clocks and the tall one was
/// pushing the short ones off the screen. Nobody has ever needed the scrip table and the furnishing
/// ranking in view at once.
/// </remarks>
internal sealed class MainWindow : Window
{
    private readonly Trades trades;
    private readonly MarketCache market;
    private readonly Balances balances;
    private readonly PricingScope scope;
    private readonly FurnishingSweep sweep;
    private readonly VendorSweep vendorSweep;
    private readonly StatusStrip strip;
    private readonly ConvertTab convert;
    private readonly CraftTab crafts;
    private readonly VendorTab vendor;
    private readonly SettingsTab settings;
    private readonly Configuration config;
    private readonly Action save;

    private Tab? pending;
    private bool restoreAttempted;
    private long persistedSweepAt;

    public MainWindow(
        Trades trades,
        MarketCache market,
        Balances balances,
        PricingScope scope,
        GatherBuddyIpc gatherBuddy,
        ItemCells cells,
        Places places,
        LiveMarket live,
        Diagnostics diagnostics,
        FurnishingSweep sweep,
        VendorSweep vendorSweep,
        ConvertTab convert,
        CraftTab crafts,
        VendorTab vendor,
        SettingsTab settings,
        Configuration config,
        Action save)
        : base("Rowena###rowena-main")
    {
        this.trades = trades;
        this.market = market;
        this.balances = balances;
        this.scope = scope;
        this.sweep = sweep;
        this.vendorSweep = vendorSweep;
        this.convert = convert;
        this.crafts = crafts;
        this.vendor = vendor;
        this.settings = settings;
        this.config = config;
        this.save = save;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(820, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        strip = new StatusStrip(market, balances, trades, gatherBuddy, cells, places, live, diagnostics, () => RefreshPrices());
    }

    /// <summary>
    /// Refetches every catalogue item. The strip's button, and the settings tab's reload,
    /// which swaps trades in that no book has been fetched for yet.
    /// </summary>
    public void RefreshPrices(FetchPriority priority = FetchPriority.Interactive) =>
        RefreshCatalogue(scope.Buying, scope.Selling, force: true, priority);

    /// <summary>
    /// Refetches one trade's items, for checking the board before committing gil to it.
    /// </summary>
    /// <remarks>
    /// Forced past the TTL, because the click is a statement of distrust in the cache and
    /// answering it from the cache would be absurd. Quietly does nothing while a fetch is
    /// already running, which the strip is already saying.
    /// </remarks>
    public void RefreshTrade(Conversion conversion)
    {
        market.RefreshInBackground(scope.Buying, ItemIds(conversion.Inputs), true, FetchPriority.Interactive);
        market.RefreshInBackground(scope.Selling, ItemIds(conversion.Outputs), true, FetchPriority.Interactive);
    }

    private static uint[] ItemIds(IReadOnlyList<ResourceAmount> side) =>
    [
        .. side
            .Where(amount => amount.Resource.Kind == ResourceKind.Item)
            .Select(amount => amount.Resource.Id)
            .Distinct(),
    ];

    public override void Draw()
    {
        var buying = scope.Buying;
        var selling = scope.Selling;

        strip.Draw(buying, selling);
        ImGui.Separator();

        if (buying is not null && selling is not null)
        {
            // Prices saved by a previous session, as soon as there is a board to compare them against.
            market.RestoreOnce(config.SweepAge());
            RestoreSweepOnce(buying, selling);

            // Free: the summaries a scan needs are already on disk with the rest of the cache,
            // so a shortlist can be rebuilt without a single request.
            vendorSweep.RestoreOnce(buying, config.VendorCandidatesToCost);
            PersistFinishedSweep();
        }

        if (!ImGui.BeginTabBar("rowena-tabs"))
            return;

        // The label carries the tab's identity in ImGui unless it is told otherwise, so every one of
        // these pins its own id after ###. Without that, a label that counts something would hand the
        // tab a new identity each time the count changed, and reset the selection along with it.
        if (ImGui.BeginTabItem("Sinks###sinks", Selecting(Tab.Sinks)))
        {
            if (buying is null || selling is null)
                NoBoard();
            else
                convert.DrawSinks();

            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Flips###flips", Selecting(Tab.Flips)))
        {
            if (buying is null || selling is null)
                NoBoard();
            else
                convert.DrawFlips();

            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(vendor.Label, Selecting(Tab.Vendor)))
        {
            if (buying is { } vendorBuying)
                vendor.Draw(vendorBuying);
            else
                NoBoard();

            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(crafts.Label, Selecting(Tab.Craft)))
        {
            if (buying is { } craftBuying && selling is { } craftSelling)
                crafts.Draw(craftBuying, craftSelling);
            else
                NoBoard();

            ImGui.EndTabItem();
        }

        // Reachable without a board, unlike the other two, since typing a world in here is what you
        // would be doing if the game could not tell you one.
        if (ImGui.BeginTabItem("Settings###settings", Selecting(Tab.Settings)))
        {
            settings.Draw();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();

        // Cleared after the whole bar, not when it matches: the flag has to survive long enough for
        // every tab to have been offered it, and the tab that wanted it has already read it.
        pending = null;
    }

    /// <summary>Opens the window on a particular tab, for the callers that mean a specific one.</summary>
    public void Show(Tab tab)
    {
        pending = tab;
        IsOpen = true;
    }

    private ImGuiTabItemFlags Selecting(Tab tab) =>
        pending == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

    internal enum Tab
    {
        Sinks,
        Flips,
        Vendor,
        Craft,
        Settings,
    }

    /// <summary>
    /// Said by a tab that cannot answer anything without a board to price against.
    /// </summary>
    /// <remarks>
    /// Inside the tab rather than in place of the tabs, so that a tab which does still work while
    /// logged out stays reachable. Typing a world in by hand is exactly what you would be doing here
    /// if the game were not running.
    /// </remarks>
    private static void NoBoard() =>
        ImGui.TextColored(Palette.Bad, "Not logged in, so there is no board to price against.");

    /// <summary>
    /// Rebuilds the last sweep's shortlist from what came back off disk.
    /// </summary>
    /// <remarks>
    /// Attempted once. A restore that finds nothing usable leaves the sweep idle, and retrying that
    /// every frame would walk the recipe sheet forever.
    /// </remarks>
    private void RestoreSweepOnce(string buying, string selling)
    {
        if (restoreAttempted || market.RestoredSweep is not { } stored)
            return;

        restoreAttempted = true;
        sweep.Restore(stored, buying, selling);
    }

    /// <summary>
    /// Refreshes the catalogue's items, each on the board it belongs to.
    /// </summary>
    /// <remarks>
    /// Inputs and outputs go to different places, and an item can be both, so this is two passes
    /// rather than one over a merged list. Only the trades you could run are priced: the whole
    /// generated catalogue is a thousand ids, and most belong to currencies you hold none of.
    /// </remarks>
    private void RefreshCatalogue(
        string? buying,
        string? selling,
        bool force = false,
        FetchPriority priority = FetchPriority.Background)
    {
        var (bought, sold) = trades.Relevant(balances.Held);

        market.RefreshInBackground(buying, bought, force, priority);
        market.RefreshInBackground(selling, sold, force, priority);
    }

    /// <summary>
    /// Writes a finished sweep out once, rather than trusting it to survive to Dispose.
    /// </summary>
    /// <remarks>
    /// On a background task: it is a few hundred kilobytes of gzip and has no business inside a
    /// frame.
    /// </remarks>
    private void PersistFinishedSweep()
    {
        if (sweep.Snapshot() is not { } snapshot || snapshot.At == persistedSweepAt)
            return;

        persistedSweepAt = snapshot.At;
        _ = Task.Run(() => market.Persist(snapshot));
    }

    /// <summary>Called on open so a stale window fills itself in without being asked.</summary>
    public override void OnOpen() => RefreshCatalogue(scope.Buying, scope.Selling);

    public override void OnClose() => save();

}
